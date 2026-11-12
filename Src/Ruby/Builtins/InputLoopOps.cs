/* ****************************************************************************
 *
 * Kernel#chomp, #chop, #sub and #gsub - the four methods that exist only when the
 * program was started with -n or -p.
 *
 * They are not written in Ruby, and cannot be: each one reads and writes $_, which
 * is frame-local, so a `def chomp` would see its own empty frame rather than the
 * caller's line. (That is true of MRI as well; it is why these are C functions
 * there.) A C# method declares `RubyScope scope` and is handed the caller's scope,
 * the same way Kernel#gets threads it to store the line it read.
 *
 * They are also not registered through [RubyMethod]: the attribute would put them
 * in the Kernel method table unconditionally, and `Kernel.private_instance_methods(false)`
 * must not list them unless -n or -p was passed. RubyCommandLine calls Define below
 * once it knows.
 *
 * ***************************************************************************/

using System;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    public static class InputLoopOps {
        /// <summary>
        /// Adds the four methods to Kernel, private as instance methods and public on the
        /// singleton - what `module_function` would give them - which is where MRI puts them.
        /// </summary>
        internal static void Define(RubyContext/*!*/ context) {
            var kernel = context.KernelModule;
            var singleton = kernel.GetOrCreateSingletonClass();

            DefineBoth(kernel, singleton, "chomp", 0x80000000U,
                new Func<CallSiteStorage<Func<CallSite, object, RubyArray, object>>, RubyScope, object, object[], object>(Chomp));

            DefineBoth(kernel, singleton, "chop", 0x00000000U,
                new Func<CallSiteStorage<Func<CallSite, object, object>>, RubyScope, object, object>(Chop));

            DefineBoth(kernel, singleton, "sub", 0x80000000U,
                new Func<CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>, RubyScope, BlockParam, object, object[], object>(Sub));

            DefineBoth(kernel, singleton, "gsub", 0x80000000U,
                new Func<CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>, RubyScope, BlockParam, object, object[], object>(Gsub));
        }

        private static void DefineBoth(RubyModule/*!*/ kernel, RubyModule/*!*/ singleton, string/*!*/ name,
            uint overloadAttributes, Delegate/*!*/ overload) {

            LibraryInitializer.DefineLibraryMethod(kernel, name,
                (int)(RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.NoEvent), overloadAttributes, overload);
            LibraryInitializer.DefineLibraryMethod(singleton, name,
                (int)(RubyMethodAttributes.PublicInstance | RubyMethodAttributes.NoEvent), overloadAttributes, overload);
        }

        /// <summary>
        /// The current $_, which has to be a String: every one of these methods sends a String
        /// method to it, and MRI checks the type up front rather than letting NoMethodError out.
        /// </summary>
        private static object/*!*/ GetInputLine(RubyContext/*!*/ context, RubyScope/*!*/ scope) {
            object line = scope.GetInnerMostClosureScope().LastInputLine;
            if (!(line is MutableString)) {
                throw RubyExceptions.CreateTypeError(String.Format("$_ value need to be String ({0} given)",
                    line == null ? "nil" : context.GetClassDisplayName(line)
                ));
            }
            return line;
        }

        private static object SetInputLine(RubyScope/*!*/ scope, object value) {
            return scope.GetInnerMostClosureScope().LastInputLine = value;
        }

        // The arguments are splatted rather than spelled out as overloads so that the arity
        // error for a bad call comes from String#chomp/#sub/#gsub and reads the way MRI's does.

        public static object Chomp(CallSiteStorage<Func<CallSite, object, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, params object[]/*!*/ args) {

            object line = GetInputLine(storage.Context, scope);
            var site = storage.Context.GetOrCreateSendSite<Func<CallSite, object, RubyArray, object>>(
                "chomp", new RubyCallSignature(0, RubyCallFlags.HasSplattedArgument)
            );
            return SetInputLine(scope, site.Target(site, line, RubyOps.MakeArrayN(args)));
        }

        public static object Chop(CallSiteStorage<Func<CallSite, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self) {

            object line = GetInputLine(storage.Context, scope);
            var site = storage.GetCallSite("chop", 0);
            return SetInputLine(scope, site.Target(site, line));
        }

        public static object Sub(CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, params object[]/*!*/ args) {

            return Substitute(storage, scope, block, "sub", args);
        }

        public static object Gsub(CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, params object[]/*!*/ args) {

            return Substitute(storage, scope, block, "gsub", args);
        }

        private static object Substitute(CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, string/*!*/ methodName, object[]/*!*/ args) {

            object line = GetInputLine(storage.Context, scope);
            var site = storage.Context.GetOrCreateSendSite<Func<CallSite, object, Proc, RubyArray, object>>(
                methodName, new RubyCallSignature(0, RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock)
            );
            return SetInputLine(scope, site.Target(site, line, (block != null) ? block.Proc : null, RubyOps.MakeArrayN(args)));
        }
    }
}

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
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using System.Runtime.CompilerServices;

namespace IronRuby.Builtins {

    /// <summary>
    /// Methods on Singleton(main).
    /// </summary>
    [RubyModule(RubyClass.MainSingletonName)]
    public static class MainSingletonOps {
        // No #initialize of its own: MRI's main inherits BasicObject's, so send(:initialize)
        // answers nil and send(:initialize, 1) is an ArgumentError, where a stub here threw
        // NotImplementedException.

        #region Public Instance Methods

        // The top-level object answers "main" to both, as MRI's does: Kernel#inspect no longer
        // falls back to #to_s, so without its own #inspect main would report itself as
        // "#<Object:0x...>" in NoMethodError messages and under Kernel#p.
        [RubyMethod("to_s", RubyMethodAttributes.PublicInstance)]
        [RubyMethod("inspect", RubyMethodAttributes.PublicInstance)]
        public static MutableString/*!*/ ToS(object/*!*/ self) {
            return MutableString.CreateAscii("main");
        }

        // main's #public, #private and #include are private, as MRI's are: `self.include M` works,
        // `TOPLEVEL_BINDING.receiver.include M` does not (#138).

        // thread-safe:
        // Like Module#public: a single Array argument lists the names, and the result is what was passed.
        [RubyMethod("public", RubyMethodAttributes.PrivateInstance)]
        public static object SetPublicVisibility(ConversionStorage<string>/*!*/ stringCast, RubyScope/*!*/ scope, object/*!*/ self,
            params object[]/*!*/ methodNames) {

            SetVisibility(scope, self, ModuleOps.ToMethodNames(stringCast, methodNames), RubyMethodAttributes.PublicInstance);
            return ModuleOps.VisibilityResult(methodNames);
        }

        // thread-safe:
        [RubyMethod("private", RubyMethodAttributes.PrivateInstance)]
        public static object SetPrivateVisibility(ConversionStorage<string>/*!*/ stringCast, RubyScope/*!*/ scope, object/*!*/ self,
            params object[]/*!*/ methodNames) {

            SetVisibility(scope, self, ModuleOps.ToMethodNames(stringCast, methodNames), RubyMethodAttributes.PrivateInstance);
            return ModuleOps.VisibilityResult(methodNames);
        }

        private static RubyModule/*!*/ SetVisibility(RubyScope/*!*/ scope, object/*!*/ self, string/*!*/[]/*!*/ methodNames, RubyMethodAttributes attributes) {
            Assert.NotNull(scope, self, methodNames);
            RubyModule module;

            // MRI: Method is searched in the class of self (Object), not in the main singleton class.
            // IronRuby specific: If we are in a top-level scope with redirected method lookup module we use that module (hosted scopes).
            var topScope = scope.Top.GlobalScope.TopLocalScope;
            if (scope == topScope && topScope.MethodLookupModule != null) {
                module = topScope.MethodLookupModule;
            } else {
                module = scope.RubyContext.GetClassOf(self);
            }
            ModuleOps.SetMethodAttributes(scope, module, methodNames, attributes);
            return module;
        }

        /// <summary>
        /// The module a definition at the top level lands in: Object, or whatever a hosted scope
        /// redirects method lookup to. The same one `private' and `public' work on up there.
        /// </summary>
        private static RubyModule/*!*/ GetDefinitionModule(RubyScope/*!*/ scope, object/*!*/ self) {
            var topScope = scope.Top.GlobalScope.TopLocalScope;
            if (scope == topScope && topScope.MethodLookupModule != null) {
                return topScope.MethodLookupModule;
            }
            return scope.RubyContext.GetClassOf(self);
        }

        /// <summary>
        /// A method `def' writes at the top level is private, but one main.define_method writes is
        /// public - MRI's define_method always is, and at the top level it is main's own method
        /// rather than Module's that runs.
        /// </summary>
        private static RubySymbol/*!*/ MakePublic(RubyScope/*!*/ scope, RubyModule/*!*/ module, RubySymbol/*!*/ name) {
            ModuleOps.SetMethodAttributes(scope, module, new[] { name.ToString() }, RubyMethodAttributes.PublicInstance);
            return name;
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName) {

            var module = GetDefinitionModule(scope, self);
            return MakePublic(scope, module, ModuleOps.DefineMethod(scope, block, module, methodName));
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]Proc/*!*/ method) {

            var module = GetDefinitionModule(scope, self);
            return MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {

            var module = GetDefinitionModule(scope, self);
            return MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {

            var module = GetDefinitionModule(scope, self);
            return MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
        }

        /// <summary>
        /// main.using activates a module's refinements for the rest of the current file or eval string.
        /// It is only legal at the top level: CRuby refuses to let a method body activate refinements,
        /// because the activation would be for the caller's lexical scope rather than its own.
        /// </summary>
        [RubyMethod("using", RubyMethodAttributes.PrivateInstance)]
        public static object Using(RubyScope/*!*/ scope, object/*!*/ self, object module) {
            for (RubyScope s = scope; s != null; s = s.Parent) {
                // A class or module body is not the top level either: `module X; MAIN.send(:using,
                // m); end' activates for X's body, not for main's file, which is not what the
                // caller can have meant.
                if (s.Kind == ScopeKind.Method || s.Kind == ScopeKind.BlockMethod || s.Kind == ScopeKind.Module) {
                    throw RubyExceptions.CreateRuntimeError("main.using is permitted only at toplevel");
                }
                if (s.Kind == ScopeKind.TopLevel) {
                    break;
                }
            }

            ModuleOps.ActivateRefinements(scope, module);
            return self;
        }

        // thread-safe:
        [RubyMethod("include", RubyMethodAttributes.PrivateInstance)]
        public static RubyModule/*!*/ Include(
            CallSiteStorage<Func<CallSite, RubyModule, RubyModule, object>>/*!*/ appendFeaturesStorage,
            CallSiteStorage<Func<CallSite, RubyModule, RubyModule, object>>/*!*/ includedStorage,
            RubyScope/*!*/ scope, object/*!*/ self, [NotNullItems]params RubyModule/*!*/[]/*!*/ modules) {

            // MRI: in a file loaded with wrapping the modules go into the wrapper module, not Object
            RubyModule result = scope.Top.Module ?? scope.RubyContext.GetClassOf(self);

            // Through Module#include rather than straight into the ancestor list, because the
            // #append_features and #included hooks are where a module does its real work:
            // ActiveSupport::Concern installs its ClassMethods there, so a top-level
            // `include SomeConcern` that skipped them kept only half the module. ActionView's
            // SanitizeHelper is one of those, and #sanitize in a plain script then failed.
            return ModuleOps.Include(appendFeaturesStorage, includedStorage, result, modules);
        }

        #endregion
    }
}

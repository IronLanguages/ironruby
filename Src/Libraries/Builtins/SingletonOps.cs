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
using IronRuby.Runtime.Calls;
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

            // MRI: Method is searched in the class of self (Object), not in the main singleton class.
            // IronRuby specific: If we are in a top-level scope with redirected method lookup module we use that module (hosted scopes).
            RubyModule mirror;
            RubyModule module = GetDefinitionModule(scope, self, out mirror);
            ModuleOps.SetMethodAttributes(scope, module, methodNames, attributes);

            // ... and Object as well, where CRuby changes it, for each method Object has (issue 177); a singleton method of
            // main (`def self.x') only main's singleton class has is changed there alone, as before
            if (mirror != null && methodNames.Length > 0) {
                var mirrored = Array.FindAll(methodNames,
                    (name) => mirror.ResolveMethod(name, VisibilityContext.AllVisible).Found);
                if (mirrored.Length > 0) {
                    ModuleOps.SetMethodAttributes(scope, mirror, mirrored, attributes);
                }
            }
            return module;
        }

        /// <summary>
        /// The module a definition at the top level lands in: Object, or whatever a hosted scope
        /// redirects method lookup to. The same one `private' and `public' work on up there.
        /// For the latter, <paramref name="mirror"/> is Object, which the definition goes to as
        /// well, as a top-level `def' does there (RubyOps.GetHostedTopLevelMirror); null otherwise.
        /// </summary>
        private static RubyModule/*!*/ GetDefinitionModule(RubyScope/*!*/ scope, object/*!*/ self, out RubyModule mirror) {
            var topScope = scope.Top.GlobalScope.TopLocalScope;
            if (scope == topScope && topScope.MethodLookupModule != null) {
                mirror = scope.RubyContext.ObjectClass;
                return topScope.MethodLookupModule;
            }
            mirror = null;
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

        /// <summary>
        /// main.define_method with a Method or UnboundMethod in a host's scope: what goes to Object besides main's singleton
        /// class. The scope's copy of a top-level `def' there is Object's method, so Object gets the same method from its own
        /// copy. A singleton method of main (`def self.x') is not one Object can take - CRuby raises TypeError for it - and is
        /// defined in main's singleton class alone, as it was before.
        /// </summary>
        private static void DefineMirroredMethod(RubyScope/*!*/ scope, RubyModule/*!*/ module, RubyModule/*!*/ mirror,
            string/*!*/ methodName, RubyMemberInfo/*!*/ info, string/*!*/ sourceName) {

            if (info.DeclaringModule == module || info.DeclaringModule.IsSingletonClass) {
                var objectInfo = mirror.ResolveMethod(sourceName, VisibilityContext.AllVisible).Info;
                if (objectInfo == null || !objectInfo.IsEquivalentTo(info)) {
                    return;
                }
                info = objectInfo;
            }
            ModuleOps.DefineMethod(scope, mirror, methodName, info, mirror, sourceName);
            MakePublic(scope, mirror, scope.RubyContext.CreateSymbol(methodName, RubyEncoding.UTF8));
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName) {

            return DefineMethod(scope, self, methodName, block.Proc);
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]Proc/*!*/ method) {

            RubyModule mirror;
            var module = GetDefinitionModule(scope, self, out mirror);
            var result = MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
            if (mirror != null) {
                MakePublic(scope, mirror, ModuleOps.DefineMethod(scope, mirror, methodName, method));
            }
            return result;
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {

            RubyModule mirror;
            var module = GetDefinitionModule(scope, self, out mirror);
            var result = MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
            if (mirror != null) {
                DefineMirroredMethod(scope, module, mirror, methodName, method.Info, method.Name);
            }
            return result;
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {

            RubyModule mirror;
            var module = GetDefinitionModule(scope, self, out mirror);
            var result = MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
            if (mirror != null) {
                DefineMirroredMethod(scope, module, mirror, methodName, method.Info, method.Name);
            }
            return result;
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

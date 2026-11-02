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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting;
using System.Diagnostics;

namespace IronRuby.Builtins {

    // MRI undefines Module.allocate (object.c: rb_undef_method(rb_singleton_class(rb_cModule),
    // "allocate")), so a module comes only from Module.new; ClassOps defines Class.allocate again.
    [RubyClass("Module", Extends = typeof(RubyModule), Inherits = typeof(Object), Restrictions = ModuleRestrictions.Builtin | ModuleRestrictions.NoUnderlyingType)]
    [UndefineMethod("allocate", IsStatic = true)]
    public static class ModuleOps {

        #region initialize, initialize_copy

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static object Reinitialize(BlockParam block, RubyModule/*!*/ self) {
            // no class can be reinitialized:
            if (self.IsClass) {
                throw RubyExceptions.CreateTypeError("already initialized class");
            }

            return (block != null) ? RubyUtils.EvaluateInModule(self, block, new object[] { self }) : null;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static RubyModule/*!*/ InitializeCopy(RubyModule/*!*/ self, object other) {
            // no class can be reinitialized:
            if (self.IsClass) {
                throw RubyExceptions.CreateTypeError("already initialized class");
            }
            
            // self could be a meta-module:
            RubyClass selfClass = self.Context.GetClassOf(self);
            RubyClass otherClass = self.Context.GetClassOf(other);
            if (otherClass != selfClass) {
                throw RubyExceptions.CreateTypeError("initialize_copy should take same class object");
            }

            self.InitializeModuleCopy((RubyModule)other);
            return self;
        }
        
        #endregion

        #region extend_object, extended, include, included

        // thread-safe:
        [RubyMethod("extend_object", RubyMethodAttributes.PrivateInstance)]
        public static object ExtendObject(RubyModule/*!*/ self, object extendedObject) {
            // include self into extendedObject's singleton
            RubyUtils.RequireDefinableSingleton(extendedObject);
            self.Context.GetOrCreateSingletonClass(extendedObject).IncludeModules(self);
            return extendedObject;
        }

        [RubyMethod("extended", RubyMethodAttributes.PrivateInstance)]
        public static void ObjectExtended(RubyModule/*!*/ self, object extendedObject) {
            // extendedObject has been extended by self, i.e. self has been included into extendedObject's singleton class
        }

        // public since Ruby 2.1
        [RubyMethod("include")]
        public static RubyModule/*!*/ Include(
            CallSiteStorage<Func<CallSite, RubyModule, RubyModule, object>>/*!*/ appendFeaturesStorage,
            CallSiteStorage<Func<CallSite, RubyModule, RubyModule, object>>/*!*/ includedStorage,
            RubyModule/*!*/ self, [NotNullItems]params RubyModule/*!*/[]/*!*/ modules) {

            if (modules.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 0, expected 1+)");
            }

            foreach (var module in modules) {
                if (module.IsRefinement) {
                    throw RubyExceptions.CreateTypeError("Cannot include refinement");
                }
            }

            RubyUtils.RequireMixins(self, modules);

            var appendFeatures = appendFeaturesStorage.GetCallSite("append_features", 1);
            var included = includedStorage.GetCallSite("included", 1);

            // Kernel#append_features inserts the module at the beginning of ancestors list;
            // ancestors after include: [modules[0], modules[1], ..., modules[N-1], self, ...]
            for (int i = modules.Length - 1; i >= 0; i--) {
                appendFeatures.Target(appendFeatures, modules[i], self);
                included.Target(included, modules[i], self);
            }

            return self;
        }

        [RubyMethod("included", RubyMethodAttributes.PrivateInstance)]
        public static void Included(RubyModule/*!*/ self, RubyModule/*!*/ owner) {
            // self has been included into owner
        }

        // thread-safe:
        [RubyMethod("append_features", RubyMethodAttributes.PrivateInstance)]
        public static RubyModule/*!*/ AppendFeatures(RubyModule/*!*/ self, [NotNull]RubyModule/*!*/ owner) {
            owner.IncludeModules(self);
            return self;
        }

        #endregion

        #region prepend, prepended, prepend_features

        [RubyMethod("prepend")]
        public static RubyModule/*!*/ Prepend(
            CallSiteStorage<Func<CallSite, RubyModule, RubyModule, object>>/*!*/ prependFeaturesStorage,
            CallSiteStorage<Func<CallSite, RubyModule, RubyModule, object>>/*!*/ prependedStorage,
            RubyModule/*!*/ self, params object[]/*!*/ args) {

            if (args.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 0, expected 1+)");
            }

            // reported by name rather than by the generic conversion failure, the way MRI does it:
            var modules = new RubyModule[args.Length];
            for (int i = 0; i < args.Length; i++) {
                modules[i] = args[i] as RubyModule;
                if (modules[i] == null && args[i] != null) {
                    throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Module)",
                        self.Context.GetClassDisplayName(args[i])
                    );
                }
            }

            foreach (var module in modules) {
                if (module != null && module.IsRefinement) {
                    throw RubyExceptions.CreateTypeError("Cannot prepend refinement");
                }
            }

            RubyUtils.RequirePrepends(self, modules);

            var prependFeatures = prependFeaturesStorage.GetCallSite("prepend_features", 1);
            var prepended = prependedStorage.GetCallSite("prepended", 1);

            // Module#prepend_features inserts the module in front of the receiver;
            // ancestors after `prepend a, b': [a, b, self, ...]
            for (int i = modules.Length - 1; i >= 0; i--) {
                prependFeatures.Target(prependFeatures, modules[i], self);
                prepended.Target(prepended, modules[i], self);
            }

            return self;
        }

        [RubyMethod("prepended", RubyMethodAttributes.PrivateInstance)]
        public static void Prepended(RubyModule/*!*/ self, RubyModule/*!*/ owner) {
            // self has been prepended to owner
        }

        // thread-safe:
        [RubyMethod("prepend_features", RubyMethodAttributes.PrivateInstance)]
        public static RubyModule/*!*/ PrependFeatures(RubyModule/*!*/ self, [NotNull]RubyModule/*!*/ owner) {
            owner.PrependModules(self);
            return self;
        }

        #endregion

        #region refine, using, refinements, used_modules, used_refinements

        [RubyMethod("refine", RubyMethodAttributes.PrivateInstance)]
        public static RubyModule/*!*/ Refine(BlockParam block, RubyModule/*!*/ self, object target) {
            RubyModule refinedModule = target as RubyModule;
            if (refinedModule == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Class or Module)",
                    self.Context.GetClassDisplayName(target)
                );
            }
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("no block given");
            }

            RubyModule refinement = self.GetOrCreateRefinement(refinedModule);

            // The block runs with self set to the refinement, like module_eval, so `def' inside it defines
            // a method on the refinement rather than on the refined class; and the enclosing module's
            // refinements are active inside it.
            RubyUtils.EvaluateRefinementBlock(self, refinement, block);
            return refinement;
        }

        /// <summary>
        /// Module#using activates a module's refinements for the rest of the *caller's lexical scope*.
        /// The RubyScope the library binder hands us is that scope, and its parent chain is the lexical
        /// nesting, so recording the activation on it is all the scoping there is.
        /// </summary>
        [RubyMethod("using", RubyMethodAttributes.PrivateInstance)]
        public static RubyModule/*!*/ Using(RubyScope/*!*/ scope, RubyModule/*!*/ self, object module) {
            // MRI refuses it anywhere inside a method body, blocks and module bodies included
            if (scope.GetInnerMostMethodScope() != null) {
                throw RubyExceptions.CreateRuntimeError("Module#using is not permitted in methods");
            }
            ActivateRefinements(scope, module);
            return self;
        }

        internal static void ActivateRefinements(RubyScope/*!*/ scope, object module) {
            RubyModule refiner = module as RubyModule;
            if (refiner == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Module)",
                    scope.RubyContext.GetClassDisplayName(module)
                );
            }

            // A CLR namespace brings in its extension methods where it is used, as C#'s `using System.Linq;'
            // does, and a static class those it declares (C#'s `using static').
            string ns;
            Type staticClass;
            if (RubyModule.IsClrExtensionSource(refiner, out ns, out staticClass)) {
                if (refiner.Context != scope.RubyContext) {
                    throw RubyExceptions.CreateTypeError("Cannot use '{0}' defined in a foreign runtime #{1}", refiner.Name, refiner.Context.RuntimeId);
                }
                scope.RubyContext.ActivateScopedExtensions(ns ?? staticClass.Namespace ?? String.Empty);
                scope.ActivateRefinements(refiner, true);
                return;
            }

            if (refiner.IsClass) {
                throw RubyExceptions.CreateTypeError("wrong argument type Class (expected Module)");
            }

            // `using' is a no-op for a module that refines nothing, exactly as in CRuby.
            scope.ActivateRefinements(refiner, true);
        }

        [RubyMethod("refinements")]
        public static RubyArray/*!*/ GetRefinements(RubyModule/*!*/ self) {
            var refinements = new List<RubyModule>();
            self.GetOwnRefinements(refinements);
            var result = new RubyArray(refinements.Count);
            foreach (RubyModule m in refinements) {
                result.Add(m);
            }
            return result;
        }

        [RubyMethod("used_modules", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetUsedModules(RubyScope/*!*/ scope, RubyModule/*!*/ self) {
            var modules = new List<RubyModule>();
            scope.GetActiveRefinements().GetUsedModules(modules);
            var result = new RubyArray(modules.Count);
            foreach (RubyModule m in modules) {
                result.Add(m);
            }
            return result;
        }

        [RubyMethod("used_refinements", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetUsedRefinements(RubyScope/*!*/ scope, RubyModule/*!*/ self) {
            var refinements = new List<RubyModule>();
            scope.GetActiveRefinements().GetUsedRefinements(refinements);
            var result = new RubyArray(refinements.Count);
            foreach (RubyModule m in refinements) {
                result.Add(m);
            }
            return result;
        }

        #endregion

        #region private, protected, public, private_class_method, public_class_method, module_function

        /// <summary>
        /// The names a visibility modifier was given. Since Ruby 3.0 a single Array argument is
        /// the list of names - `private [:a, :b]` - so it is spread here; everything else is
        /// converted with the usual symbol-or-string protocol.
        /// </summary>
        internal static string/*!*/[]/*!*/ ToMethodNames(ConversionStorage<string>/*!*/ stringCast, object[]/*!*/ argv) {
            object[] names = argv;
            if (argv.Length == 1 && argv[0] is System.Collections.IList list) {
                names = new object[list.Count];
                for (int i = 0; i < list.Count; i++) {
                    names[i] = list[i];
                }
            }

            var result = new string[names.Length];
            for (int i = 0; i < names.Length; i++) {
                result[i] = Protocols.CastToSymbol(stringCast, names[i]);
            }
            return result;
        }

        /// <summary>
        /// What a visibility modifier answers since Ruby 3.1: nil with no arguments, the single
        /// argument as it was passed, or an Array of the arguments. Not the module.
        /// </summary>
        internal static object VisibilityResult(object[]/*!*/ argv) {
            switch (argv.Length) {
                case 0: return null;
                case 1: return argv[0];
                default: return new RubyArray(argv);
            }
        }

        // thread-safe:
        [RubyMethod("private", RubyMethodAttributes.PrivateInstance)]
        public static object SetPrivateVisibility(ConversionStorage<string>/*!*/ stringCast,
            RubyScope/*!*/ scope, RubyModule/*!*/ self, params object[]/*!*/ methodNames) {

            // overwrites methods to instance:
            SetMethodAttributes(scope, self, ToMethodNames(stringCast, methodNames), RubyMethodAttributes.PrivateInstance);
            return VisibilityResult(methodNames);
        }

        // thread-safe:
        [RubyMethod("protected", RubyMethodAttributes.PrivateInstance)]
        public static object SetProtectedVisibility(ConversionStorage<string>/*!*/ stringCast,
            RubyScope/*!*/ scope, RubyModule/*!*/ self, params object[]/*!*/ methodNames) {
            // overwrites methods to instance:
            SetMethodAttributes(scope, self, ToMethodNames(stringCast, methodNames), RubyMethodAttributes.ProtectedInstance);
            return VisibilityResult(methodNames);
        }

        // thread-safe:
        [RubyMethod("public", RubyMethodAttributes.PrivateInstance)]
        public static object SetPublicVisibility(ConversionStorage<string>/*!*/ stringCast,
            RubyScope/*!*/ scope, RubyModule/*!*/ self, params object[]/*!*/ methodNames) {
            // overwrites methods to instance:
            SetMethodAttributes(scope, self, ToMethodNames(stringCast, methodNames), RubyMethodAttributes.PublicInstance);
            return VisibilityResult(methodNames);
        }

        // thread-safe:
        [RubyMethodAttribute("private_class_method")]
        public static RubyModule/*!*/ MakeClassMethodsPrivate(ConversionStorage<string>/*!*/ stringCast,
            RubyModule/*!*/ self, params object[]/*!*/ methodNames) {
            SetMethodAttributes(self.GetOrCreateSingletonClass(), ToMethodNames(stringCast, methodNames), RubyMethodAttributes.Private);
            // unlike #private/#public, the _class_method pair answers the module
            return self;
        }

        // thread-safe:
        [RubyMethodAttribute("public_class_method")]
        public static RubyModule/*!*/ MakeClassMethodsPublic(ConversionStorage<string>/*!*/ stringCast,
            RubyModule/*!*/ self, params object[]/*!*/ methodNames) {
            SetMethodAttributes(self.GetOrCreateSingletonClass(), ToMethodNames(stringCast, methodNames), RubyMethodAttributes.Public);
            return self;
        }

        // thread-safe:
        [RubyMethod("module_function", RubyMethodAttributes.PrivateInstance)]
        public static object CopyMethodsToModuleSingleton(ConversionStorage<string>/*!*/ stringCast,
            RubyScope/*!*/ scope, RubyModule/*!*/ self, params object[]/*!*/ methodNames) {

            // This is an important restriction for correct super calls in module functions (see RubyOps.DefineMethod). 
            // MRI has it wrong. It checks just here and not in method definition.
            if (self.IsClass) {
                throw RubyExceptions.CreateTypeError("module_function must be called for modules");
            }
            
            // overwrites visibility to public:
            SetMethodAttributes(scope, self, ToMethodNames(stringCast, methodNames), RubyMethodAttributes.ModuleFunction);
            return VisibilityResult(methodNames);
        }

        internal static void SetMethodAttributes(RubyScope/*!*/ scope, RubyModule/*!*/ module, string/*!*/[]/*!*/ methodNames, RubyMethodAttributes attributes) {
            if (methodNames.Length == 0) {
                scope.GetMethodAttributesDefinitionScope().MethodAttributes = attributes;
            } else {
                SetMethodAttributes(module, methodNames, attributes);
            }
        }

        internal static void SetMethodAttributes(RubyModule/*!*/ module, string/*!*/[]/*!*/ methodNames, RubyMethodAttributes attributes) {
            var context = module.Context;

            bool isModuleFunction = (attributes & RubyMethodAttributes.ModuleFunction) == RubyMethodAttributes.ModuleFunction;
            var instanceVisibility = isModuleFunction ? RubyMethodVisibility.Private : 
                (RubyMethodVisibility)(attributes & RubyMethodAttributes.VisibilityMask);

            foreach (string methodName in methodNames) {
                RubyMemberInfo method;
                bool copiedIntoModule;

                // we need to define new methods one by one since the method_added events can define a new method that might be used here:
                using (context.ClassHierarchyLocker()) {
                    MethodLookup options = MethodLookup.FallbackToObject;
                    if (!isModuleFunction) {
                        options |= MethodLookup.ReturnForwarder;
                    }

                    var resolved = module.ResolveMethodNoLock(methodName, VisibilityContext.AllVisible, options);
                    method = resolved.Info;
                    if (method == null) {
                        throw RubyExceptions.CreateUndefinedMethodError(module, methodName);
                    }

                    // MRI only adds method to the target module if visibility differs:
                    // The module it was found in, rather than the module the method reports - a
                    // method copied here by an earlier visibility change resolves as a forwarder
                    // that still names the module it came from.
                    copiedIntoModule = method.Visibility != instanceVisibility && resolved.Owner != module;
                    if (method.Visibility != instanceVisibility) {
                        module.SetVisibilityNoEventNoLock(context, methodName, method, instanceVisibility);
                    }

                    if (isModuleFunction) {
                        module.SetModuleFunctionNoEventNoLock(context, methodName, method);
                    }
                }

                // #method_added is about a method appearing in this module, not about its
                // visibility: `private :foo' on a method the module already has does not call it,
                // and on an inherited one it calls it once - for the copy that lands here - and
                // not again when the visibility is changed back. module_function copies the
                // method onto the singleton, and that is a #singleton_method_added.
                if (copiedIntoModule && !isModuleFunction) {
                    module.MethodAdded(methodName);
                }

                if (isModuleFunction) {
                    module.GetOrCreateSingletonClass().MethodAdded(methodName);
                }
            }
        }

        #endregion

        #region define_method (thread-safe)

        // thread-safe:
        /// <summary>
        /// Records the ruby2_keywords flag on a method, and answers whether the method is one the
        /// flag can mean anything for - a Ruby method whose parameters are a bare rest. The
        /// prelude's Module#ruby2_keywords does the argument checking and the warning; this is
        /// the part that has to reach the method body, which is where the flag lives so that
        /// every alias of the method shares it.
        /// </summary>
        [RubyMethod("__ir_mark_ruby2_keywords__", RubyMethodAttributes.PrivateInstance)]
        public static bool MarkRuby2Keywords(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ methodName) {
            var info = self.ResolveMethod(methodName, VisibilityContext.AllVisible).Info;

            var method = info as RubyMethodInfo;
            if (method != null) {
                return method.TrySetRuby2Keywords();
            }

            // define_method makes one of these out of a block; the flag lives on the block.
            var lambda = info as RubyLambdaMethodInfo;
            return lambda != null && lambda.TrySetRuby2Keywords();
        }

        // Anything that is not a Proc, Method or UnboundMethod: MRI names the three acceptable
        // types rather than reporting a failed conversion to one of them.
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, object method) {
            throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Proc/Method/UnboundMethod)",
                scope.RubyContext.GetClassDisplayName(method));
        }

        // thread-safe:
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self, 
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {

            DefineMethod(scope, self, methodName, method.Info,  method.GetTargetClass(), method.Name);
            return scope.RubyContext.CreateSymbol(methodName, RubyEncoding.UTF8);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {
            var result = DefineMethod(scope, self, methodName.MangledName, method);
            if (methodName.HasMangledName) {
                self.AddMethodAlias(methodName.ActualName, methodName.MangledName);
            }
            return result;
        }

        // thread-safe:
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self, 
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {

            DefineMethod(scope, self, methodName, method.Info, method.TargetConstraint, method.Name);
            return scope.RubyContext.CreateSymbol(methodName, RubyEncoding.UTF8);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {
            var result = DefineMethod(scope, self, methodName.MangledName, method);
            if (methodName.HasMangledName) {
                self.AddMethodAlias(methodName.ActualName, methodName.MangledName);
            }
            return result;
        }

        private static void DefineMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self, string/*!*/ methodName, RubyMemberInfo/*!*/ info,
            RubyModule/*!*/ targetConstraint, string sourceName = null) {

            bool isModuleFunction;
            var visibility = GetDefinedMethodVisibility(scope, self, methodName, out isModuleFunction);
            using (self.Context.ClassHierarchyLocker()) {
                // MRI 1.8 does the check when the method is called, 1.9 checks it upfront as we do.
                // Since Ruby 3.0 (Feature #15608) a method whose owner is a module rather than a class may be
                // bound to any receiver, so only a class constraint is enforced.
                // The constraint is the *owner* of the method, not the receiver #instance_method
                // was sent to: Object.instance_method(:class) is owned by Kernel and so binds
                // anywhere, even though it was fetched through Object.
                var owner = info.DeclaringModule;
                if (owner != null && (!owner.IsClass || owner.IsSingletonClass)) {
                    targetConstraint = owner;
                }
                if (targetConstraint.IsClass && !self.HasAncestorNoLock(targetConstraint)) {
                    if (targetConstraint.IsSingletonClass) {
                        throw RubyExceptions.CreateTypeError("can't bind singleton method to a different class");
                    }
                    throw RubyExceptions.CreateTypeError(
                        "bind argument must be a subclass of {0}", targetConstraint.GetName(scope.RubyContext)
                    );
                }

                self.SetDefinedMethodNoEventNoLock(self.Context, methodName, info, visibility, sourceName);

                if (isModuleFunction) {
                    self.SetModuleFunctionNoEventNoLock(self.Context, methodName, info);
                }
            }

            self.MethodAdded(methodName);

            if (isModuleFunction) {
                self.GetOrCreateSingletonClass().MethodAdded(methodName);
            }
        }

        // thread-safe:
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, 
            RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ methodName) {

            return DefineMethod(scope, self, methodName, block.Proc);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block,
            RubyModule/*!*/ self, [NotNull]ClrName/*!*/ methodName) {

            var result = DefineMethod(scope, block, self, methodName.MangledName);
            if (methodName.HasMangledName) {
                self.AddMethodAlias(methodName.ActualName, methodName.MangledName);
            }
            return result;
        }

        // thread-safe:
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self, 
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]Proc/*!*/ block) {

            bool isModuleFunction;
            var visibility = GetDefinedMethodVisibility(scope, self, methodName, out isModuleFunction);
            var info = Proc.ToLambdaMethodInfo(block, methodName, visibility, self);
            self.AddMethod(scope.RubyContext, methodName, info);

            if (isModuleFunction) {
                using (self.Context.ClassHierarchyLocker()) {
                    self.SetModuleFunctionNoEventNoLock(scope.RubyContext, methodName, info);
                }
                self.GetOrCreateSingletonClass().MethodAdded(methodName);
            }

            return scope.RubyContext.CreateSymbol(methodName, RubyEncoding.UTF8);
        }

        // thread-safe:
        [RubyMethod("define_method")]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]Proc/*!*/ block) {

            var result = DefineMethod(scope, self, methodName.MangledName, block);
            if (methodName.HasMangledName) {
                self.AddMethodAlias(methodName.ActualName, methodName.MangledName);
            }
            return result;
        }

        /// <summary>
        /// The visibility #define_method gives the method it defines, and whether #module_function
        /// is in effect for it.
        ///
        /// The enclosing visibility modifier counts only when #define_method is defining on the
        /// scope's own module: `other.define_method(...)` inside a `private` or `module_function`
        /// body still defines a plain public method on `other`.  Special names (#initialize and
        /// friends) are private whatever the modifier says - and so is the instance half of a
        /// module function, whose singleton half is the public one.
        /// </summary>
        private static RubyMethodVisibility GetDefinedMethodVisibility(RubyScope/*!*/ scope, RubyModule/*!*/ module, string/*!*/ methodName,
            out bool isModuleFunction) {

            isModuleFunction = false;

            RubyMethodVisibility visibility;
            var attributesScope = scope.GetMethodAttributesDefinitionScope();
            // the same module `def` in this scope would define into - GetInnerMostModuleForMethodLookup
            // is not it: inside a `Module.new { }` block it walks past the block to Object
            if (attributesScope.GetMethodDefinitionOwner() == module) {
                // module_function cannot be toggled on in a class body - Module#module_function
                // raises there - but a module function is still meaningless on one, so guard it.
                isModuleFunction = !module.IsClass
                    && (attributesScope.MethodAttributes & RubyMethodAttributes.ModuleFunction) == RubyMethodAttributes.ModuleFunction;
                visibility = isModuleFunction ? RubyMethodVisibility.Private : attributesScope.Visibility;
            } else {
                visibility = RubyMethodVisibility.Public;
            }

            return RubyUtils.GetSpecialMethodVisibility(visibility, methodName);
        }

        #endregion

        #region method_(added|removed|undefined)

        [RubyMethod("method_added", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static void MethodAdded(object/*!*/ self, object methodName) {
            // nop
        }

        [RubyMethod("method_removed", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static void MethodRemoved(object/*!*/ self, object methodName) {
            // nop
        }

        [RubyMethod("method_undefined", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static void MethodUndefined(object/*!*/ self, object methodName) {
            // nop
        }

        #endregion

        #region attr, attr_{reader|writer|accessor} (thread-safe)

        /// <summary>
        /// Defines the accessor(s) and appends their names to <paramref name="definedNames"/>,
        /// which is what Ruby 3.0's attr/attr_reader/attr_writer/attr_accessor return.
        /// </summary>
        private static void DefineAccessor(RubyScope/*!*/ scope, RubyModule/*!*/ self, string/*!*/ name, bool readable, bool writable,
            RubyArray definedNames = null) {
            // MRI: ignores ModuleFunction scope flag (doesn't create singleton methods):

            if (!Tokenizer.IsVariableName(name)) {
                throw RubyExceptions.CreateNameError("invalid attribute name '{0}'", name);
            }

            var varName = "@" + name;
            var attributesScope = scope.GetMethodAttributesDefinitionScope();

            // The empty scope stands for "there is no Ruby frame here": it is what a conversion
            // site hands a library method, which is how `attr_reader` is reached by
            // `%i(a b).each(&method(:attr_reader))` - the &-conversion calls Method#to_proc
            // through a site with no scope of its own.  Its attributes are private, because
            // top-level `def` is, and taking them left reline's Reline::Core readers private
            // where MRI's are public.  MRI reads the visibility only off a class or module
            // body; anywhere else attr_reader defines public methods.
            //
            // The same holds for a frame whose self is some other object: MRI's rb_attr takes the
            // scope's visibility only when the caller's self is the module itself
            // (rb_vm_cref_in_context). importmap-rails does
            // `Rails::Application.send(:attr_accessor, :importmap)` at the top level of a file,
            // where the default is private, and Rails then calls `app.importmap = ...`.
            var visibility = (attributesScope.IsEmpty || !ReferenceEquals(attributesScope.SelfObject, self))
                ? RubyMethodVisibility.Public : attributesScope.Visibility;

            if (definedNames != null) {
                var context = scope.RubyContext;
                if (readable) {
                    definedNames.Add(context.CreateSymbol(name, RubyEncoding.UTF8));
                }
                if (writable) {
                    definedNames.Add(context.CreateSymbol(name + "=", RubyEncoding.UTF8));
                }
            }

            if (readable) {
                var flags = (RubyMemberFlags)RubyUtils.GetSpecialMethodVisibility(visibility, name);
                self.AddMethod(scope.RubyContext, name, new RubyAttributeReaderInfo(flags, self, varName));
            }
            
            if (writable) {
                self.AddMethod(scope.RubyContext, name + "=", new RubyAttributeWriterInfo((RubyMemberFlags)visibility, self, varName));
            }
        }

        // thread-safe:
        [RubyMethod("attr")]
        public static RubyArray/*!*/ Attr(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name) {
            var result = new RubyArray();
            DefineAccessor(scope, self, name, true, false, result);
            return result;
        }

        // thread-safe:
        [RubyMethod("attr")]
        public static RubyArray/*!*/ Attr(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name, bool writable) {
            scope.RubyContext.ReportWarning("optional boolean argument is obsoleted", true);
            var result = new RubyArray();
            DefineAccessor(scope, self, name, true, writable, result);
            return result;
        }

        // thread-safe:
        [RubyMethod("attr")]
        public static RubyArray/*!*/ Attr(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ names) {
            var result = new RubyArray();
            foreach (string name in names) {
                DefineAccessor(scope, self, name, true, false, result);
            }
            return result;
        }

        // thread-safe:
        [RubyMethod("attr_accessor")]
        public static RubyArray/*!*/ AttrAccessor(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name) {
            var result = new RubyArray();
            DefineAccessor(scope, self, name, true, true, result);
            return result;
        }

        // thread-safe:
        [RubyMethod("attr_accessor")]
        public static RubyArray/*!*/ AttrAccessor(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ names) {
            var result = new RubyArray();
            foreach (string name in names) {
                DefineAccessor(scope, self, name, true, true, result);
            }
            return result;
        }

        // thread-safe:
        [RubyMethod("attr_reader")]
        public static RubyArray/*!*/ AttrReader(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name) {
            var result = new RubyArray();
            DefineAccessor(scope, self, name, true, false, result);
            return result;
        }

        // thread-safe:
        [RubyMethod("attr_reader")]
        public static RubyArray/*!*/ AttrReader(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ names) {
            var result = new RubyArray();
            foreach (string name in names) {
                DefineAccessor(scope, self, name, true, false, result);
            }
            return result;
        }

        // thread-safe:
        [RubyMethod("attr_writer")]
        public static RubyArray/*!*/ AttrWriter(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name) {
            var result = new RubyArray();
            DefineAccessor(scope, self, name, false, true, result);
            return result;
        }

        // thread-safe:
        [RubyMethod("attr_writer")]
        public static RubyArray/*!*/ AttrWriter(RubyScope/*!*/ scope, RubyModule/*!*/ self, [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ names) {
            var result = new RubyArray();
            foreach (string name in names) {
                DefineAccessor(scope, self, name, false, true, result);
            }
            return result;
        }

        #endregion

        #region alias_method, remove_method, undef_method

        // thread-safe:
        // public since Ruby 3.0; returns the new method's name as a Symbol, like define_method
        [RubyMethod("alias_method")]
        public static object AliasMethod(ConversionStorage<MutableString>/*!*/ toStr, RubyModule/*!*/ self,
            object newName, [DefaultProtocol, NotNull]string/*!*/ oldName) {

            string name;
            var symbol = newName as RubySymbol;
            if (symbol != null) {
                name = symbol.ToString();
            } else {
                var str = newName as MutableString ?? (newName != null ? Protocols.TryCastToString(toStr, newName) : null);
                if (str == null) {
                    throw RubyOps.CreateNotSymbolNorStringError(toStr.Context, newName);
                }
                // A String name is interned as a Symbol, so one with bytes invalid in its encoding
                // raises EncodingError the way String#to_sym does.
                MutableStringOps.ToSymbol(toStr.Context, str);
                name = str.ConvertToString();
            }
            return AddMethodAlias(toStr.Context, self, name, oldName);
        }

        private static object AddMethodAlias(RubyContext/*!*/ context, RubyModule/*!*/ self, string/*!*/ newName, string/*!*/ oldName) {
            self.AddMethodAlias(newName, oldName);
            return context.CreateSymbol(newName, RubyEncoding.UTF8);
        }

        /// <summary>
        /// MRI checks frozen-ness before it looks the method up, so undef_method/remove_method on a
        /// frozen module raise FrozenError even when the name does not exist.
        /// </summary>
        private static void RequireMutable(RubyModule/*!*/ self) {
            if (!self.IsFrozen) {
                return;
            }
            var cls = self as RubyClass;
            if (cls != null && cls.IsSingletonClass) {
                throw RubyExceptions.CreateObjectFrozenError(self.Context, cls.SingletonClassOf);
            }
            throw RubyExceptions.CreateObjectFrozenError(self.Context, self);
        }

        // thread-safe:
        // public since Ruby 3.0
        [RubyMethod("remove_method")]
        public static RubyModule/*!*/ RemoveMethod(RubyModule/*!*/ self, [DefaultProtocol, NotNullItems]params string[]/*!*/ methodNames) {
            if (methodNames.Length != 0) {
                RequireMutable(self);
            }
            foreach (var methodName in methodNames) {
                // MRI: reports a warning and allows removal
                if (self.IsBasicObjectClass && methodName == Symbols.Initialize) {
                    throw RubyExceptions.CreateNameError("Cannot remove BasicObject#initialize");
                }

                if (!self.RemoveMethod(methodName)) {
                    // MRI's wording for remove_method differs from undef_method's:
                    //   "method 'x' not defined in C"
                    throw RubyExceptions.CreateNameError("method '{0}' not defined in {1}",
                        methodName, RubyExceptions.GetModuleErrorName(self));
                }
            }
            return self;
        }

        // thread-safe:
        // public since Ruby 3.0
        [RubyMethod("undef_method")]
        public static RubyModule/*!*/ UndefineMethod(RubyModule/*!*/ self, [DefaultProtocol, NotNullItems]params string[]/*!*/ methodNames) {
            if (methodNames.Length != 0) {
                RequireMutable(self);
            }
            foreach (var methodName in methodNames) {
                if (!self.ResolveMethod(methodName, VisibilityContext.AllVisible).Found) {
                    throw RubyExceptions.CreateUndefinedMethodError(self, methodName);
                }
                self.UndefineMethod(methodName);
            }
            return self;
        }

        #endregion


        #region <, >, <=, >=, <=>, ==, ===, ancestors, included_modules, include? (thread-safe)

        // thread-safe:
        [RubyMethod("==")]
        public static bool Equals(RubyModule/*!*/ self, object other) {
            return ReferenceEquals(self, other);
        }

        // thread-safe:
        [RubyMethod("===")]
        public static bool CaseEquals(RubyModule/*!*/ self, object other) {
            return self.Context.IsKindOf(other, self);
        }

        // thread-safe:
        [RubyMethod("<")]
        public static object IsSubclassOrIncluded(RubyModule/*!*/ self, [NotNull]RubyModule/*!*/ module) {
            if (ReferenceEquals(self, module)) {
                return ScriptingRuntimeHelpers.False;
            }

            if (self.Context != module.Context) {
                return null;
            }

            using (self.Context.ClassHierarchyLocker()) {
                if (self.HasAncestorNoLock(module)) {
                    return ScriptingRuntimeHelpers.True;
                }
                // related the other way round is `false', unrelated is `nil':
                return module.HasAncestorNoLock(self) ? ScriptingRuntimeHelpers.False : null;
            }
        }

        // thread-safe:
        [RubyMethod("<=")]
        public static object IsSubclassSameOrIncluded(RubyModule/*!*/ self, [NotNull]RubyModule/*!*/ module) {
            if (self.Context != module.Context) {
                return null;
            } 
            
            using (self.Context.ClassHierarchyLocker()) {
                if (self.HasAncestorNoLock(module)) {
                    return ScriptingRuntimeHelpers.True;
                }
                return module.HasAncestorNoLock(self) ? ScriptingRuntimeHelpers.False : null;
            }
        }

        // thread-safe:
        [RubyMethod(">")]
        public static object IsNotSubclassOrIncluded(RubyModule/*!*/ self, [NotNull]RubyModule/*!*/ module) {
            if (ReferenceEquals(self, module)) {
                return ScriptingRuntimeHelpers.False;
            }

            if (self.Context != module.Context) {
                return null;
            }

            using (self.Context.ClassHierarchyLocker()) {
                if (module.HasAncestorNoLock(self)) {
                    return ScriptingRuntimeHelpers.True;
                }
                // related the other way round is `false', unrelated is `nil':
                return self.HasAncestorNoLock(module) ? ScriptingRuntimeHelpers.False : null;
            }
        }

        // thread-safe:
        [RubyMethod(">=")]
        public static object IsNotSubclassSameOrIncluded(RubyModule/*!*/ self, [NotNull]RubyModule/*!*/ module) {
            if (self.Context != module.Context) {
                return null;
            } 
            
            using (self.Context.ClassHierarchyLocker()) {
                if (module.HasAncestorNoLock(self)) {
                    return ScriptingRuntimeHelpers.True;
                }
                return self.HasAncestorNoLock(module) ? ScriptingRuntimeHelpers.False : null;
            }
        }

        // thread-safe:
        [RubyMethod("<=>")]
        public static object Comparison(RubyModule/*!*/ self, [NotNull]RubyModule/*!*/ module) {
            if (ReferenceEquals(self, module)) {
                return ClrInteger.Zero;
            }

            if (self.Context != module.Context) {
                return null;
            }

            using (self.Context.ClassHierarchyLocker()) {
                if (self.HasAncestorNoLock(module)) {
                    return ClrInteger.MinusOne;
                }

                if (module.HasAncestorNoLock(self)) {
                    return ClrInteger.One;
                }
            }
            return null;
        }

        [RubyMethod("<=>")]
        public static object Comparison(RubyModule/*!*/ self, object module) {
            return null;
        }

        [RubyMethod("<")]
        [RubyMethod(">")]
        [RubyMethod("<=")]
        [RubyMethod(">=")]
        public static object InvalidComparison(RubyModule/*!*/ self, object module) {
            throw RubyExceptions.CreateTypeError("compared with non class/module");
        }

        // thread-safe:
        [RubyMethod("ancestors")]
        public static RubyArray/*!*/ Ancestors(RubyModule/*!*/ self) {
            RubyArray ancestors = new RubyArray();

            using (self.Context.ClassHierarchyLocker()) {
                self.ForEachAncestor(true, delegate(RubyModule/*!*/ module) {
                    // MRI lists singleton classes too: Object.new.singleton_class.ancestors starts
                    // with the singleton class itself, and String.singleton_class.ancestors walks
                    // #<Class:String>, #<Class:Object>, #<Class:BasicObject> before reaching Class.
                    if (!module.IsDummySingletonClass) {
                        ancestors.Add(module);
                    }
                    return false;
                });
            }
            return ancestors;
        }

        // thread-safe:
        [RubyMethod("included_modules")]
        public static RubyArray/*!*/ GetIncludedModules(RubyModule/*!*/ self) {
            RubyArray ancestorModules = new RubyArray();

            using (self.Context.ClassHierarchyLocker()) {
                self.ForEachAncestor(true, delegate(RubyModule/*!*/ module) {
                    if (module != self && !module.IsClass && !ancestorModules.Contains(module)) {
                        ancestorModules.Add(module);
                    }
                    return false;
                });
            }

            return ancestorModules;
        }

        // thread-safe:
        [RubyMethod("include?")]
        public static bool IncludesModule(RubyModule/*!*/ self, [NotNull]RubyModule/*!*/ other) {
            if (other.IsClass) {
                throw RubyExceptions.CreateTypeError("wrong argument type Class (expected Module)");
            }

            return other != self && self.HasAncestor(other);
        }

        #endregion

        #region (module|class)_(eval|exec)

        /// <summary>
        /// Module#module_eval / #class_eval. MRI takes either a block or a string with an optional
        /// filename and line, never both, and its arity errors name whichever of the two shapes
        /// was being used. The arguments are taken as a list and checked here for that reason:
        /// overload resolution can only report the union of the two, "expected 0..3".
        /// </summary>
        [RubyMethod("module_eval")]
        [RubyMethod("class_eval")]
        public static object Evaluate(ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyScope/*!*/ scope, BlockParam block, RubyModule/*!*/ self, params object[]/*!*/ args) {

            if (block != null) {
                if (args.Length != 0) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0)", args.Length);
                }

                // The module is passed to the block as its single argument, so
                // `Struct.new(:a) { |c| ... }` and `String.class_eval { |m| ... }` see it rather
                // than nil. module_exec/class_exec pass the caller's arguments instead.
                return RubyUtils.EvaluateInModule(self, block, new object[] { self });
            }

            if (args.Length == 0 || args.Length > 3) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..3)", args.Length);
            }

            var code = Protocols.CastToString(toStr, args[0]);
            var file = (args.Length > 1) ? Protocols.CastToString(toStr, args[1]) : null;
            var line = (args.Length > 2) ? Protocols.CastToFixnum(toInt, args[2]) : 1;

            return RubyUtils.Evaluate(code, scope, self, self, file, line);
        }

        // This method is not available in 1.8 so far, but since the usual workaround is very inefficient it is useful to have it in 1.8 as well.
        [RubyMethod("module_exec")]
        [RubyMethod("class_exec")]
        public static object Execute(BlockParam block, RubyModule/*!*/ self, params object[]/*!*/ args) {
            if (block == null) {
                throw RubyExceptions.CreateLocalJumpError("no block given");
            }
            return RubyUtils.EvaluateInModule(self, block, args);
        }

        #endregion

        #region class_variables, class_variable_defined?, class_variable_get, class_variable_set, remove_class_variable

        // not thread-safe
        [RubyMethod("class_variables")]
        public static RubyArray/*!*/ ClassVariables(RubyModule/*!*/ self, [DefaultParameterValue(true)]bool inherit) {
            var result = new RubyArray();
            var seen = new HashSet<string>();
            using (self.Context.ClassHierarchyLocker()) {
                self.ForEachClassVariable(inherit, (module, name, value) => {
                    if (name != null && seen.Add(name)) {
                        result.Add(self.Context.StringifyIdentifier(name));
                    }
                    return false;
                });
            }
            return result;
        }

        // not thread-safe:
        [RubyMethod("class_variable_defined?")]
        public static bool IsClassVariableDefined(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ variableName) {
            object value;
            if (self.TryResolveClassVariable(variableName, out value) == null) {
                RubyUtils.CheckClassVariableName(variableName);
                return false;
            }
            return true;
        }

        // not thread-safe:
        [RubyMethod("class_variable_get")]
        public static object GetClassVariable(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self, object nameArg) {
            string variableName = Protocols.CastToSymbol(stringCast, nameArg);
            object value;
            if (self.TryResolveClassVariable(variableName, out value) == null) {
                RubyUtils.CheckClassVariableName(self.Context, self, variableName, nameArg);
                throw RubyExceptions.WithNameAndReceiver(self.Context,
                    RubyExceptions.CreateNameError("uninitialized class variable {0} in {1}", variableName, self.Name),
                    variableName, self);
            }
            return value;
        }

        // not thread-safe:
        [RubyMethod("class_variable_set")]
        public static object ClassVariableSet(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ variableName, object value) {
            RubyUtils.CheckClassVariableName(variableName);
            // an ancestor that already has the variable keeps it, as for @@x = value
            object oldValue;
            (self.TryResolveClassVariable(variableName, out oldValue) ?? self).SetClassVariable(variableName, value);
            return value;
        }

        // not thread-safe:
        [RubyMethod("remove_class_variable")]
        public static object RemoveClassVariable(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ variableName) {
            object value;
            if (!self.TryGetClassVariable(variableName, out value)) {
                // MRI words a bad name differently here than class_variable_get and friends do
                if (!Tokenizer.IsClassVariableName(variableName)) {
                    throw RubyExceptions.CreateNameError("wrong class variable name {0}", variableName);
                }
                throw RubyExceptions.CreateNameError("class variable {0} not defined for {1}", variableName, self.Name);
            }
            self.RemoveClassVariable(variableName);
            return value;
        }

        #endregion

        #region constants, const_defined?, const_set, const_get, remove_const, const_missing

        // thread-safe:
        [RubyMethod("constants", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetGlobalConstants(RubyModule/*!*/ self) {
            return GetDefinedConstants(self.Context.ObjectClass, true);
        }

        // Module.constants lists the lexically visible constants only when called without arguments;
        // with an argument MRI dispatches to Module#constants on the receiver itself.
        [RubyMethod("constants", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetGlobalConstants(RubyModule/*!*/ self, bool inherited) {
            return GetDefinedConstants(self, inherited);
        }

        // thread-safe:
        [RubyMethod("constants")]
        public static RubyArray/*!*/ GetDefinedConstants(RubyModule/*!*/ self, [DefaultParameterValue(true)]bool inherited) {
            var result = new RubyArray();
            if (inherited) {
                var visited = new Dictionary<string, bool>();

                bool hideGlobalConstants = !self.IsObjectClass;

                using (self.Context.ClassHierarchyLocker()) {
                    self.ForEachConstant(true, delegate(RubyModule/*!*/ module, string name, object value) {
                        if (name == null) {
                            // terminate enumeration when Object is reached
                            return hideGlobalConstants && module.IsObjectClass;
                        }

                        // private constants are left out, and do not hide an ancestor's public one (as in MRI):
                        if (!visited.ContainsKey(name) && !module.IsPrivateConstant(name)) {
                            if (Tokenizer.IsConstantName(name)) {
                                result.Add(self.Context.CreateSymbol(name, module.GetConstantEncoding(name)));
                            }
                            visited.Add(name, true);
                        }
                        return false;
                    });
                }

            } else {
                using (self.Context.ClassHierarchyLocker()) {
                    self.EnumerateConstants((module, name, value) => {
                        if (Tokenizer.IsConstantName(name) && !module.IsPrivateConstant(name)) {
                            result.Add(self.Context.CreateSymbol(name, module.GetConstantEncoding(name)));
                        }
                        return false;
                    });
                }
            }
            return result;
        }

        // thread-safe:
        [RubyMethod("const_defined?")]
        public static bool IsConstantDefined(ConversionStorage<MutableString>/*!*/ stringCast, RubyModule/*!*/ self, object constantName) {
            return IsConstantDefined(stringCast, self, constantName, true);
        }

        [RubyMethod("const_defined?")]
        public static bool IsConstantDefined(ConversionStorage<MutableString>/*!*/ stringCast, RubyModule/*!*/ self, object constantName,
            bool inherit) {

            bool isTopLevel;
            string[] parts = SplitConstantName(stringCast, constantName, out isTopLevel);
            var context = self.Context;
            RubyModule owner = isTopLevel ? context.ObjectClass : self;

            // MRI searches Object for the first segment only (rb_const_defined vs
            // rb_const_defined_from), and only for a module: a class already has Object among its
            // ancestors, unless it is BasicObject - which is exactly the class MRI does not want
            // answering for Object's constants.
            bool lookupObject = inherit && !isTopLevel && !owner.IsClass;

            for (int i = 0; i < parts.Length; i++) {
                object value;

                // Passing null as the autoload scope so that a registered autoload answers true
                // without running: MRI reports the constant as defined but does not trigger it here.
                bool found = inherit
                    ? owner.TryResolveConstant(null, parts[i], out value)
                    : owner.TryGetConstant(null, parts[i], out value);

                if (!found && lookupObject && !owner.IsObjectClass) {
                    found = context.ObjectClass.TryResolveConstant(null, parts[i], out value);
                }
                lookupObject = false;

                if (!found) {
                    return false;
                }
                if (i == parts.Length - 1) {
                    return true;
                }

                owner = value as RubyModule;
                if (owner == null) {
                    throw RubyExceptions.CreateTypeError("{0} does not refer to class/module", String.Join("::", parts));
                }
            }

            return false;
        }

        // thread-safe:
        [RubyMethod("const_get")]
        public static object GetConstantValue(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope,
            RubyModule/*!*/ self, object constantName) {
            return GetConstantValue(stringCast, scope, self, constantName, true);
        }

        // thread-safe:
        [RubyMethod("const_get")]
        public static object GetConstantValue(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope,
            RubyModule/*!*/ self, object constantName, bool inherit) {

            bool isTopLevel;
            string[] parts = SplitConstantName(stringCast, constantName, out isTopLevel);
            var context = self.Context;
            RubyModule owner = isTopLevel ? context.ObjectClass : self;

            // MRI searches Object for the first segment only (rb_const_get vs rb_const_get_from).
            bool lookupObject = inherit && !isTopLevel;

            object value = null;
            for (int i = 0; i < parts.Length; i++) {
                if (inherit) {
                    value = RubyUtils.GetConstant(scope.GlobalScope, owner, parts[i], lookupObject);
                } else if (owner.TryGetConstant(scope.GlobalScope, parts[i], out value)) {
                    if (owner.IsDeprecatedConstant(parts[i])) {
                        context.ReportConstantDeprecation(owner, parts[i]);
                    }
                } else {
                    value = ConstantMissing(owner, parts[i]);
                }
                lookupObject = false;

                if (i == parts.Length - 1) {
                    break;
                }

                owner = value as RubyModule;
                if (owner == null) {
                    throw RubyExceptions.CreateTypeError("{0} does not refer to class/module", String.Join("::", parts));
                }
            }

            return value;
        }

        // thread-safe:
        [RubyMethod("const_set")]
        public static object SetConstantValue(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ constantName, object value) {
            RubyUtils.CheckConstantName(constantName);
            string sourcePath;
            int sourceLine;
            RubyUtils.TryGetCallerSourceLocation(self.Context, out sourcePath, out sourceLine);
            RubyUtils.SetConstant(self, constantName, value, sourcePath, sourceLine);
            return value;
        }

        // thread-safe:
        [RubyMethod("remove_const", RubyMethodAttributes.PrivateInstance)]
        public static object RemoveConstant(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ constantName) {
            bool deprecated = self.IsDeprecatedConstant(constantName);

            object value;
            if (!self.TryRemoveConstant(constantName, out value)) {
                if (!Tokenizer.IsConstantName(constantName)) {
                    throw RubyExceptions.CreateNameError("'{0}' is not allowed as a constant variable name", constantName);
                }
                throw RubyExceptions.CreateNameError("constant {0}::{1} not defined", self.Name, constantName);
            }

            if (deprecated) {
                self.Context.ReportConstantDeprecation(self, constantName);
            }
            // a pending autoload has no value yet
            return RubyModule.IsAutoload(value) ? null : value;
        }

        // public since Ruby 3.0
        [RubyMethod("private_constant")]
        public static RubyModule/*!*/ PrivateConstant(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self,
            [NotNull]params object[]/*!*/ constantNames) {
            SetConstantVisibility(stringCast, self, constantNames, true);
            return self;
        }

        // public since Ruby 3.0
        [RubyMethod("public_constant")]
        public static RubyModule/*!*/ PublicConstant(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self,
            [NotNull]params object[]/*!*/ constantNames) {
            SetConstantVisibility(stringCast, self, constantNames, false);
            return self;
        }

        // public since Ruby 3.0
        [RubyMethod("deprecate_constant")]
        public static RubyModule/*!*/ DeprecateConstant(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self,
            [NotNull]params object[]/*!*/ constantNames) {

            foreach (var constantName in constantNames) {
                var name = Protocols.CastToSymbol(stringCast, constantName);
                RubyUtils.CheckConstantName(name);
                self.SetConstantDeprecated(name);
            }
            return self;
        }

        private static void SetConstantVisibility(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self,
            object[]/*!*/ constantNames, bool isPrivate) {
            foreach (var constantName in constantNames) {
                var name = Protocols.CastToSymbol(stringCast, constantName);
                RubyUtils.CheckConstantName(name);
                self.SetConstantVisibility(name, isPrivate);
            }
        }

        [RubyMethod("const_missing")]
        public static object ConstantMissing(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name) {
            return self.Context.ResolveMissingConstant(self, name);
        }

        /// <summary>
        /// Splits a constant name given to const_get/const_source_location into its path segments.
        /// A Symbol must be a plain constant name; a String may be a scoped path ("A::B") and may be
        /// rooted at Object ("::A::B"), in which case <paramref name="isTopLevel"/> is set.
        /// </summary>
        private static string/*!*/[]/*!*/ SplitConstantName(ConversionStorage<MutableString>/*!*/ stringCast, object name, out bool isTopLevel) {
            isTopLevel = false;

            var symbol = name as RubySymbol;
            string str;
            if (symbol != null) {
                str = symbol.ToString();
            } else {
                var mstr = name as MutableString ?? Protocols.CastToString(stringCast, name);
                str = mstr.ToString();
            }

            if (symbol == null && str.StartsWith("::", StringComparison.Ordinal)) {
                isTopLevel = true;
                str = str.Substring(2);
            }

            string[] parts = (symbol == null) ? str.Split(new[] { "::" }, StringSplitOptions.None) : new[] { str };
            foreach (string part in parts) {
                if (!Tokenizer.IsConstantName(part)) {
                    // MRI names the whole path: "wrong constant name Foo::".
                    throw RubyExceptions.CreateNameError(String.Format("wrong constant name {0}", (isTopLevel ? "::" : "") + str));
                }
            }
            return parts;
        }

        // thread-safe:
        [RubyMethod("const_source_location")]
        public static object GetConstantSourceLocation(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope,
            RubyModule/*!*/ self, object constantName) {
            return GetConstantSourceLocation(stringCast, scope, self, constantName, true);
        }

        // thread-safe:
        [RubyMethod("const_source_location")]
        public static object GetConstantSourceLocation(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope,
            RubyModule/*!*/ self, object constantName, bool inherit) {

            bool isTopLevel;
            string[] parts = SplitConstantName(stringCast, constantName, out isTopLevel);
            var context = self.Context;

            RubyModule owner = isTopLevel ? context.ObjectClass : self;
            bool lookupObject = inherit && !isTopLevel;

            // Resolve everything but the last segment; only the last one's location is reported.
            for (int i = 0; i < parts.Length - 1; i++) {
                object value;
                bool found = inherit
                    ? owner.TryResolveConstant(scope.GlobalScope, parts[i], out value)
                    : owner.TryGetConstant(scope.GlobalScope, parts[i], out value);

                if (!found && lookupObject && !owner.IsObjectClass) {
                    found = context.ObjectClass.TryResolveConstant(scope.GlobalScope, parts[i], out value);
                }
                lookupObject = false;

                if (!found) {
                    return null;
                }
                owner = value as RubyModule;
                if (owner == null) {
                    throw RubyExceptions.CreateTypeError("{0} does not refer to class/module", String.Join("::", parts));
                }
                inherit = true;
            }

            string lastName = parts[parts.Length - 1];
            RubyModule definingModule = FindConstantOwner(context, scope, owner, lastName, inherit, lookupObject);
            if (definingModule == null) {
                return null;
            }

            string sourcePath;
            int sourceLine;
            var result = new RubyArray();
            if (definingModule.TryGetConstantLocation(lastName, out sourcePath, out sourceLine)) {
                result.Add(context.EncodePath(sourcePath));
                result.Add(sourceLine);
            }
            return result;
        }

        // Returns the module in the lookup path of <paramref name="owner"/> that defines the constant, or null.
        private static RubyModule FindConstantOwner(RubyContext/*!*/ context, RubyScope/*!*/ scope, RubyModule/*!*/ owner,
            string/*!*/ name, bool inherit, bool lookupObject) {

            RubyModule result = null;
            using (context.ClassHierarchyLocker()) {
                owner.ForEachConstant(inherit, (module, constantName, value) => {
                    if (constantName == name) {
                        result = module;
                        return true;
                    }
                    return false;
                });
            }

            if (result == null && lookupObject && !owner.IsObjectClass) {
                // A Module's lookup path ends at Object for reads (MRI searches Object last for modules too).
                using (context.ClassHierarchyLocker()) {
                    context.ObjectClass.ForEachConstant(true, (module, constantName, value) => {
                        if (constantName == name) {
                            result = module;
                            return true;
                        }
                        return false;
                    });
                }
            }

            return result;
        }

        [RubyMethod("const_added", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static void ConstantAdded(RubyModule/*!*/ self, object name) {
            // nop
        }

        #endregion

        #region autoload, autoload?

        // thread-safe:
        [RubyMethod("autoload")]
        public static void SetAutoloadedConstant(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ constantName, object pathArg) {

            RubyUtils.CheckConstantName(constantName);
            MutableString path = Protocols.CastToPath(toPath, pathArg);
            if (path.IsEmpty) {
                throw RubyExceptions.CreateArgumentError("empty file name");
            }

            self.SetAutoloadedConstant(constantName, path);

            string sourcePath;
            int sourceLine;
            if (RubyUtils.TryGetCallerSourceLocation(self.Context, out sourcePath, out sourceLine)) {
                self.SetConstantLocation(constantName, sourcePath, sourceLine);
            }

            self.ConstantAdded(constantName);
        }

        // thread-safe:
        [RubyMethod("autoload?")]
        public static MutableString GetAutoloadedConstantPath(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ constantName) {
            return self.GetAutoloadedConstantPath(constantName, true);
        }

        // thread-safe:
        [RubyMethod("autoload?")]
        public static MutableString GetAutoloadedConstantPath(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ constantName,
            bool inherit) {
            return self.GetAutoloadedConstantPath(constantName, inherit);
        }

        #endregion

        /// <summary>Ruby 2.1's Module#singleton_class?.</summary>
        [RubyMethod("singleton_class?")]
        public static bool IsSingletonClass(RubyModule/*!*/ self) {
            return self.IsSingletonClass && !self.IsDummySingletonClass;
        }

        #region {private_|protected_|public_|}instance_methods (thread-safe)

        // thread-safe:
        [RubyMethod("instance_methods")]
        public static RubyArray/*!*/ GetInstanceMethods(RubyModule/*!*/ self) {
            return GetInstanceMethods(self, true);
        }

        // thread-safe:
        [RubyMethod("instance_methods")]
        public static RubyArray/*!*/ GetInstanceMethods(RubyModule/*!*/ self, bool inherited) {
            return GetMethods(self, inherited, RubyMethodAttributes.PublicInstance | RubyMethodAttributes.ProtectedInstance);
        }

        // thread-safe:
        [RubyMethod("private_instance_methods")]
        public static RubyArray/*!*/ GetPrivateInstanceMethods(RubyModule/*!*/ self) {
            return GetPrivateInstanceMethods(self, true);
        }

        // thread-safe:
        [RubyMethod("private_instance_methods")]
        public static RubyArray/*!*/ GetPrivateInstanceMethods(RubyModule/*!*/ self, bool inherited) {
            return GetMethods(self, inherited, RubyMethodAttributes.PrivateInstance);
        }

        // thread-safe:
        [RubyMethod("protected_instance_methods")]
        public static RubyArray/*!*/ GetProtectedInstanceMethods(RubyModule/*!*/ self) {
            return GetProtectedInstanceMethods(self, true);
        }

        // thread-safe:
        [RubyMethod("protected_instance_methods")]
        public static RubyArray/*!*/ GetProtectedInstanceMethods(RubyModule/*!*/ self, bool inherited) {
            return GetMethods(self, inherited, RubyMethodAttributes.ProtectedInstance);
        }

        // thread-safe:
        [RubyMethod("public_instance_methods")]
        public static RubyArray/*!*/ GetPublicInstanceMethods(RubyModule/*!*/ self) {
            return GetPublicInstanceMethods(self, true);
        }

        // thread-safe:
        [RubyMethod("public_instance_methods")]
        public static RubyArray/*!*/ GetPublicInstanceMethods(RubyModule/*!*/ self, bool inherited) {
            return GetMethods(self, inherited, RubyMethodAttributes.PublicInstance);
        }

        // thread-safe:
        // Names that this module (not its ancestors) undefines with undef_method.
        [RubyMethod("undefined_instance_methods")]
        public static RubyArray/*!*/ GetUndefinedInstanceMethods(RubyModule/*!*/ self) {
            var result = new RubyArray();
            using (self.Context.ClassHierarchyLocker()) {
                self.EnumerateMethods((module, name, member) => {
                    if (member.IsUndefined) {
                        result.Add(self.Context.StringifyIdentifier(name));
                    }
                    return false;
                });
            }
            return result;
        }

        internal static RubyArray/*!*/ GetMethods(RubyModule/*!*/ self, bool inherited, RubyMethodAttributes attributes) {
            return GetMethods(self, inherited, attributes, null);
        }

        internal static RubyArray/*!*/ GetMethods(RubyModule/*!*/ self, bool inherited, RubyMethodAttributes attributes,
            IEnumerable<string> foreignMembers) {
            return GetMethods(self, inherited, attributes, foreignMembers, false);
        }

        // objectMethods: see RubyModule.ForEachMember
        internal static RubyArray/*!*/ GetMethods(RubyModule/*!*/ self, bool inherited, RubyMethodAttributes attributes,
            IEnumerable<string> foreignMembers, bool objectMethods) {

            var result = new RubyArray();
            // A CLR member is listed under its mangled name, which a Ruby method further up the ancestors (Enumerable#count
            // behind List#Count) can share. Calls go to the first one, so only that one is listed.
            var listed = new HashSet<string>();
            using (self.Context.ClassHierarchyLocker()) {
                self.ForEachMember(inherited, attributes, foreignMembers, objectMethods, (name, module, member) => {
                    if (member.IsInteropMember && (module.Restrictions & ModuleRestrictions.NoNameMapping) == 0 && RubyUtils.HasMangledName(name)) {
                        if (Tokenizer.IsMethodName(name) || Tokenizer.IsOperatorName(name)) {
                            var clrName = new ClrName(name);
                            if (listed.Add(clrName.MangledName)) {
                                result.Add(clrName);
                            }
                        }
                    } else if (listed.Add(name)) {
                        result.Add(self.Context.StringifyIdentifier(name));
                    }
                });
            }

            // A refinement's superclass is, in effect, the class it refines: what that class has
            // is listed after the refinement's own methods.
            if (inherited && self.IsRefinement) {
                var seen = new HashSet<object>(result);
                foreach (object name in GetMethods(self.RefinedModule, true, attributes, foreignMembers, objectMethods)) {
                    if (seen.Add(name)) {
                        result.Add(name);
                    }
                }
            }
            return result;
        }

        #endregion

        #region {private_|protected_|public_|}method_defined? (thread-safe)

        // The visibility a method_defined? query sees for the name, or None if it has no definition. It is
        // the visibility at the name's entry point - the forwarder's, for a `public :m' re-exporting an
        // inherited m - and, as in MRI (check_definition_visibility), a not-implemented method has none.
        // inherit = false counts only a definition in the module itself, not in a prepended module.
        private static RubyMethodVisibility GetDefinitionVisibility(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self,
            object name, bool inherit) {

            // MRI raises TypeError for anything that is not a Symbol or a String; the
            // [DefaultProtocol]string binding quietly turned an Integer into its digits.
            string methodName = Protocols.CastToSymbol(stringCast, name);
            var method = inherit ? self.GetMethodLookup(methodName) : self.GetOwnMethodLookup(methodName);
            if (!method.Found) {
                return RubyMethodVisibility.None;
            }
            var libraryMethod = method.Info as RubyLibraryMethodInfo;
            return (libraryMethod != null && libraryMethod.IsNotImplemented) ? RubyMethodVisibility.None : method.Visibility;
        }

        // thread-safe:
        [RubyMethod("method_defined?")]
        public static bool MethodDefined(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self, object name,
            [DefaultParameterValue(true)]bool inherit) {
            var visibility = GetDefinitionVisibility(stringCast, self, name, inherit);
            return visibility == RubyMethodVisibility.Public || visibility == RubyMethodVisibility.Protected;
        }

        // thread-safe:
        [RubyMethod("private_method_defined?")]
        public static bool PrivateMethodDefined(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self, object name,
            [DefaultParameterValue(true)]bool inherit) {
            return GetDefinitionVisibility(stringCast, self, name, inherit) == RubyMethodVisibility.Private;
        }

        // thread-safe:
        [RubyMethod("protected_method_defined?")]
        public static bool ProtectedMethodDefined(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self, object name,
            [DefaultParameterValue(true)]bool inherit) {
            return GetDefinitionVisibility(stringCast, self, name, inherit) == RubyMethodVisibility.Protected;
        }

        // thread-safe:
        [RubyMethod("public_method_defined?")]
        public static bool PublicMethodDefined(ConversionStorage<string>/*!*/ stringCast, RubyModule/*!*/ self, object name,
            [DefaultParameterValue(true)]bool inherit) {
            return GetDefinitionVisibility(stringCast, self, name, inherit) == RubyMethodVisibility.Public;
        }

        #endregion

        #region instance_method, 1.9: public_instance_method

        // thread-safe:
        [RubyMethod("instance_method")]
        public static UnboundMethod/*!*/ GetInstanceMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName) {

            // A refinement active where this is called is active for it too: MRI answers the
            // refined method, whose #owner is the refinement.
            RubyMemberInfo method = self.ResolveMethodWithRefinements(methodName, VisibilityContext.AllVisible, scope).Info;
            if (method == null) {
                throw RubyExceptions.CreateUndefinedMethodError(self, methodName);
            }

            RubyModule constraint = self;
            if (self.IsSingletonClass && method.DeclaringModule != self) {
                constraint = ((RubyClass)self).SuperClass;
            }

            // unbound method binable to any class with "constraint" mixin:
            return new UnboundMethod(constraint, methodName, method);
        }

        // thread-safe:
        [RubyMethod("public_instance_method")]
        public static UnboundMethod/*!*/ GetPublicInstanceMethod(RubyScope/*!*/ scope, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName) {

            RubyMemberInfo method = self.ResolveMethodWithRefinements(methodName, VisibilityContext.AllVisible, scope).Info;
            if (method == null) {
                throw RubyExceptions.CreateUndefinedMethodError(self, methodName);
            }
            if (method.Visibility != RubyMethodVisibility.Public) {
                // the method is there, just not public - which is what MRI says
                throw RubyExceptions.CreateNameError("method '{0}' for {1} '{2}' is {3}", methodName,
                    self.IsClass ? "class" : "module", self.GetDisplayName(scope.RubyContext, false),
                    method.Visibility == RubyMethodVisibility.Private ? "private" : "protected");
            }

            return GetInstanceMethod(scope, self, methodName);
        }

        #endregion

        #region to_s, name, freeze

        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static MutableString/*!*/ ToS(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            return self.GetDisplayName(context, false);
        }

        [RubyMethod("name")]
        public static MutableString GetName(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            return self.GetNameString(context);
        }

        [RubyMethod("freeze")]
        public static RubyModule/*!*/ Freeze(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            self.Freeze();
            return self;
        }

        // True if the string is a constant path ("A", "A::B", "::A::B"), which set_temporary_name rejects
        // to keep temporary names distinguishable from real ones.
        private static bool IsConstantPath(string/*!*/ name) {
            if (name.StartsWith("::", StringComparison.Ordinal)) {
                name = name.Substring(2);
            }
            foreach (string segment in name.Split(new[] { "::" }, StringSplitOptions.None)) {
                if (!Tokenizer.IsConstantName(segment)) {
                    return false;
                }
            }
            return true;
        }

        [RubyMethod("set_temporary_name")]
        public static RubyModule/*!*/ SetTemporaryName(RubyContext/*!*/ context, RubyModule/*!*/ self, [DefaultProtocol]MutableString name) {
            if (self.HasPermanentName) {
                throw RubyExceptions.CreateRuntimeError("can't change permanent name");
            }

            if (name == null) {
                self.SetName(null, false);
                return self;
            }

            string str = name.ConvertToString();
            if (str.Length == 0) {
                throw RubyExceptions.CreateArgumentError("empty class/module name");
            }
            if (IsConstantPath(str)) {
                throw RubyExceptions.CreateArgumentError("the temporary name must not be a constant path to avoid confusion");
            }

            self.SetName(str, false);
            return self;
        }

        #endregion

        #region IronRuby: to_clr_type, of, []

        [RubyMethod("to_clr_type")]
        public static Type ToClrType(RubyModule/*!*/ self) {
            return self.TypeTracker != null ? self.TypeTracker.Type : null;
        }

        [RubyMethod("to_clr_ref")]
        public static RubyModule ToClrRef(RubyModule/*!*/ self) {
            try {
                return self.TypeTracker != null ? self.Context.GetClass(self.TypeTracker.Type.MakeByRefType()) : null;
            } catch (Exception) {
                throw RubyExceptions.CreateTypeError(
                    "Cannot create by-ref type for '{0}'", self.Context.GetTypeName(self.TypeTracker.Type, true)
                );
            }
        }

        [RubyMethod("of")]
        [RubyMethod("[]")]
        public static RubyModule/*!*/ Of(RubyModule/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ typeArgs) {
            if (self.TypeTracker == null) {
                throw RubyExceptions.CreateArgumentError("'{0}' is not a type", self.Name);
            }

            Type type = self.TypeTracker.Type;
            int provided = typeArgs.Length;

            if (provided == 1 && type == typeof(Array)) {
                Type elementType = Protocols.ToType(self.Context, typeArgs[0]);
                Type arrayType;
                try {
                    arrayType = elementType.MakeArrayType();
                } catch (Exception) {
                    throw RubyExceptions.CreateTypeError(
                        "Cannot create array type for '{0}'", self.Context.GetTypeName(elementType, true)
                    );
                }
                return self.Context.GetModule(arrayType);
            }

            if (!type.IsGenericTypeDefinition) {
                if (provided > 0) {
                    throw RubyExceptions.CreateArgumentError("'{0}' is not a generic type definition", self.Name);
                }
                return self;
            }

            int required = type.GetGenericArguments().Length;
            if (required != provided) {
                throw RubyExceptions.CreateArgumentError("Type '{0}' requires {1} generic type arguments, {2} provided", self.Name, required, provided);
            }

            Type concreteType = type.MakeGenericType(Protocols.ToTypes(self.Context, typeArgs));
            return self.Context.GetModule(concreteType);
        }

        [RubyMethod("of")]
        [RubyMethod("[]")]
        public static RubyModule/*!*/ Of(RubyModule/*!*/ self, int genericArity) {
            if (self.TypeTracker == null) {
                throw RubyExceptions.CreateArgumentError("'{0}' is not a type", self.Name);
            }

            Type type = self.TypeTracker.Type;

            if (!type.IsGenericTypeDefinition) {
                if (genericArity > 0) {
                    throw RubyExceptions.CreateArgumentError("'{0}' is not a generic type definition", self.Name);
                }
                return self;
            }
            
            if (type.GetGenericArguments().Length != genericArity) {
                throw RubyExceptions.CreateArgumentError("'{0}' does not have generic arity {1}", self.Name, genericArity);
            }

            return self;
        }

        #endregion

        
        #region nesting

        [RubyMethod("nesting", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetLexicalModuleNesting(RubyScope/*!*/ scope, RubyModule/*!*/ self) {
            RubyArray result = new RubyArray();
            do {
                // Ruby 1.9: the anonymous module doesn't show up
                if (scope.Module != null) {
                    result.Add(scope.Module);
                }
                scope = (RubyScope)scope.Parent;
            } while (scope != null);
            return result;
        }

        #endregion
    }
}

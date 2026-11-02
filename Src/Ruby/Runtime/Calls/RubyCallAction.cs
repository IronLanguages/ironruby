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

#if FEATURE_CORE_DLR
using System.Linq.Expressions;
#else
using Microsoft.Scripting.Ast;
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Reflection;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Generation;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Calls {
    using AstFactory = IronRuby.Compiler.Ast.AstFactory;
    using Ast = Expression;

    public class RubyCallAction : RubyMetaBinder {
        private readonly RubyCallSignature _signature;
        private readonly string/*!*/ _methodName;

        public override RubyCallSignature Signature {
            get { return _signature; }
        }

        public string/*!*/ MethodName {
            get { return _methodName; }
        }

        public override Type/*!*/ ReturnType {
            get { return _signature.ResolveOnly ? typeof(bool) : typeof(object); }
        }

        internal protected RubyCallAction(RubyContext context, string/*!*/ methodName, RubyCallSignature signature) 
            : base(context) {
            Assert.NotNull(methodName);
            
            // a virtual call cannot be a super call nor interop call:
            Debug.Assert(!signature.IsVirtualCall || !signature.IsSuperCall && !signature.IsInteropCall);

            // a super call must have implicit self:
            Debug.Assert(!signature.IsSuperCall || signature.HasImplicitSelf);

            _methodName = methodName;
            _signature = signature;
        }

        /// <summary>
        /// Creates a runtime-bound call site binder.
        /// </summary>
        public static RubyCallAction/*!*/ Make(RubyContext/*!*/ context, string/*!*/ methodName, int argumentCount) {
            return Make(context, methodName, RubyCallSignature.Simple(argumentCount));
        }

        /// <summary>
        /// Creates a runtime-bound call site binder.
        /// </summary>
        public static RubyCallAction/*!*/ Make(RubyContext/*!*/ context, string/*!*/ methodName, RubyCallSignature signature) {
            ContractUtils.RequiresNotNull(context, "context");
            ContractUtils.RequiresNotNull(methodName, "methodName");
            return context.MetaBinderFactory.Call(methodName, signature);
        }

        /// <summary>
        /// Creates a call site binder that can be used from multiple runtimes. The site it binds for can be called from multiple runtimes.
        /// </summary>
        [Emitted]
        public static RubyCallAction/*!*/ MakeShared(string/*!*/ methodName, RubyCallSignature signature) {
            // TODO: reduce usage of these sites to minimum
            return RubyMetaBinderFactory.Shared.Call(methodName, signature);
        }

        public override string/*!*/ ToString() {
            return _methodName + _signature.ToString() + (Context != null ? " @" + Context.RuntimeId.ToString() : null);
        }

        public override Expression/*!*/ CreateExpression() {
            return Expression.Call(
                Methods.GetMethod(typeof(RubyCallAction), "MakeShared", typeof(string), typeof(RubyCallSignature)),
                AstUtils.Constant(_methodName),
                _signature.CreateExpression()
            );
        }

        #region Precompiled Rules

        internal override object BindPrecompiled(Type/*!*/ delegateType, object[]/*!*/ args, bool allowLibraryDispatch) {
            // IsVariableCall only decides which error a call that finds no method raises; a dispatcher
            // is only made for a method that was found, and hands every other case back to the
            // binder. Leaving it out of the mask sent every bare `foo` to a rule of its own.
            if (Context == null ||
                Signature.ResolveOnly ||
                (Signature.Flags & ~(RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasScope | RubyCallFlags.HasBlock | RubyCallFlags.HasRhsArgument | RubyCallFlags.HasSourceBlock | RubyCallFlags.IsVariableCall)) != 0) {
                return null;
            }

            // A precompiled dispatcher carries no conditions beyond the class version, so it cannot hold
            // the lexical refinement guard.  Once anything has been refined we fall back to rule building.
            if (Context.HasRefinements) {
                return null;
            }

            // Nor can it hold RubyMethodInfo.BuildCallNoFlow's unmarking of a keyword-arguments
            // hash handed to a method that declares no keywords. A call site's syntax decides
            // whether it passes keywords, so seeing one here means every call at this site
            // passes one: unless the method takes keywords (checked below), hand the site to the
            // rule builder instead of precompiling it.
            bool passesKeywords = false;
            for (int i = 0; i < args.Length; i++) {
                if (RubyOps.IsKeywordArgumentsHash(args[i])) {
                    passesKeywords = true;
                    break;
                }
            }

            RubyScope scope;
            object target;
            if (Signature.HasScope) {
                scope = (RubyScope)args[0];
                target = args[1];
            } else {
                scope = Context.EmptyScope;
                target = args[0];
            }

            int version;
            MethodResolutionResult method;
            bool plainClrTypeTest;
            RubyClass targetClass = Context.GetImmediateClassOf(target);
            using (targetClass.Context.ClassHierarchyLocker()) {
                // initialization changes the version (see MetaObjectBuilder.AddTargetTypeTest):
                targetClass.InitializeMethodsNoLock();
                version = targetClass.Version.Method;
                method = targetClass.ResolveMethodForSiteNoLock(_methodName, GetVisibilityContext(Signature, scope));
                plainClrTypeTest = target != null && !(target is IRubyObject) && HasPlainClrTypeTest(targetClass, _methodName);
            }

            if (!method.Found || method.Info.IsProtected && !Signature.HasImplicitSelf) {
                return null;
            }

            // the rule has to check for an ignored block (see BuildCall)
            if (Signature.HasSourceBlock && RequiresUnusedBlockCheck(method.Info)) {
                return null;
            }

            // a call through an alias has to tell the method its name (see RubyMethodInfo.BuildCallNoFlow)
            var rubyMethod = method.Info as RubyMethodInfo;
            if (rubyMethod != null && rubyMethod.DefinitionName != _methodName) {
                return null;
            }

            // a method that takes or refuses keywords reads the hash as it is - nothing to unmark
            if (passesKeywords && (rubyMethod == null || !rubyMethod.TakesOrRefusesKeywords)) {
                return null;
            }

            var dispatcher = method.Info.GetDispatcher(delegateType, Signature, target, version);

            // a Ruby method of a builtin class (much of the core library is written in Ruby), called on nil, true, false or a CLR object:
            if (dispatcher == null && rubyMethod != null && !(target is IRubyObject) && (target == null || target is bool || plainClrTypeTest) &&
                !IsForeignObject(target)) {
                dispatcher = rubyMethod.GetClrDispatcher(delegateType, Signature, target, targetClass, version);
            }
            if (dispatcher != null) {
                object result = dispatcher.CreateDelegate(MethodDispatcher.UntypedFuncs.Contains(delegateType));
                RubyBinder.DumpPrecompiledRule(this, dispatcher);
                return result;
            }

            // The first calls of a library method from a site go without a rule. A rule built for a call is used for one
            // call more often than not - most of the prelude runs once - and building it costs more than many such calls.
            // Kernel#send is bound as the call it makes (DispatchIntrinsics.TryBuildSend), and so are calls while c_call
            // is traced (the rule reports the call). The same goes for Class#new of a plain Ruby class.
            var custom = method.Info as RubyCustomMethodInfo;
            if (allowLibraryDispatch && custom != null && custom.IsInstanceConstructor && target is RubyClass && !passesKeywords) {
                return LibraryDispatcher.TryCreateConstruction(delegateType, Signature, (RubyClass)target, targetClass, version, args);
            }

            var libraryMethod = method.Info as RubyLibraryMethodInfo;
            if (allowLibraryDispatch && libraryMethod != null && !TracePoint.CCallTracing && !DispatchIntrinsics.IsSend(libraryMethod)) {
                return LibraryDispatcher.TryCreate(this, delegateType, Signature, libraryMethod, targetClass, target, plainClrTypeTest, version, args);
            }

            return null;
        }

        #endregion

        #region Shared Rules

        // A rule that serves receivers of several classes, or of one class in several states, that resolve the call to the same method.
        // The first rule bound for a method is an ordinary one (tests a constant version); the second class (version) to resolve the call
        // to that method gets a rule that tests the class version against a set (see RuleVersionSet), and every further one is added to
        // the set once the call has been resolved for it again - with no rule built or compiled.
        private sealed class SharedRule {
            internal readonly RubyMemberInfo/*!*/ Member;
            internal readonly Type/*!*/ TargetType;
            internal readonly Type/*!*/ DelegateType;
            internal readonly int FirstVersion;

            // null until the shared rule is built:
            internal RuleVersionSet Versions;
            internal volatile object Rule;

            internal SharedRule(RubyMemberInfo/*!*/ member, Type/*!*/ targetType, Type/*!*/ delegateType, int firstVersion) {
                Member = member;
                TargetType = targetType;
                DelegateType = delegateType;
                FirstVersion = firstVersion;
            }
        }

        private const int MaxSharedRules = 16;

        private const RubyCallFlags SharableCallFlags = RubyCallFlags.HasScope | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasRhsArgument |
            RubyCallFlags.HasBlock | RubyCallFlags.HasImplicitSelf | RubyCallFlags.IsVariableCall | RubyCallFlags.HasSourceBlock;

        private static readonly object _SharedRulesLock = new object();
        private SharedRule[] _sharedRules;

        // Handed from BindSharedRule to Build of the same binder:
        [ThreadStatic]
        private static RuleVersionSet _PendingVersionSet;
        [ThreadStatic]
        private static RubyCallAction _PendingVersionSetOwner;

        internal override T BindShared<T>(object[]/*!*/ args) {
            RubyContext context = Context;
            if (context == null || Signature.ResolveOnly || (Signature.Flags & ~SharableCallFlags) != 0 ||
                context.HasRefinements || TracePoint.CCallTracing) {
                return null;
            }

            RubyScope scope;
            object target;
            if (Signature.HasScope) {
                scope = (RubyScope)args[0];
                target = args[1];
            } else {
                scope = context.EmptyScope;
                target = args[0];
            }

            // The type test of a Ruby object is only a version check (see MetaObjectBuilder.AddTargetTypeTest), and so is that of nil
            // and of a CLR object whose class has neither singletons nor instances of a Ruby subclass. Not true/false: they are 
            // told apart by value.
            Type targetType;
            if (target == null) {
                targetType = typeof(DynamicNull);
            } else if (target is bool) {
                return null;
            } else {
                targetType = target.GetType();
            }

            // an object of another runtime:
            RubyClass targetClass = context.GetImmediateClassOf(target);
            if (targetClass.Context != context) {
                return null;
            }

            int version;
            MethodResolutionResult method;
            using (context.ClassHierarchyLocker()) {
                targetClass.InitializeMethodsNoLock();
                if (target != null && !(target is IRubyObject) && !HasPlainClrTypeTest(targetClass, _methodName)) {
                    return null;
                }
                version = targetClass.Version.Method;
                method = targetClass.ResolveMethodForSiteNoLock(_methodName, GetVisibilityContext(Signature, scope), MethodLookup.Default);
            }

            // Rules calling these methods depend on nothing but the method, the call signature and the arguments.
            // A protected method's rule also tests the caller's class.
            if (!method.Found || method.Info.IsProtected ||
                method.Info.GetType() != typeof(RubyLibraryMethodInfo) && method.Info.GetType() != typeof(RubyMethodInfo)) {
                return null;
            }

            // respond_to?(:sym) and send(:sym) rules are specialized on the receiver's class (DispatchIntrinsics):
            if (DispatchIntrinsics.MaySpecialize(method.Info)) {
                return null;
            }

            SharedRule shared = null;
            var sharedRules = _sharedRules;
            if (sharedRules != null) {
                foreach (var rule in sharedRules) {
                    if (rule.Member == method.Info && rule.TargetType == targetType && rule.DelegateType == typeof(T)) {
                        shared = rule;
                        break;
                    }
                }
            }

            if (shared == null) {
                lock (_SharedRulesLock) {
                    sharedRules = _sharedRules;
                    if (sharedRules == null || sharedRules.Length < MaxSharedRules) {
                        _sharedRules = ArrayUtils.Append(sharedRules ?? new SharedRule[0], new SharedRule(method.Info, targetType, typeof(T), version));
                    }
                }
                return null;
            }

            object sharedRule = shared.Rule;
            if (sharedRule == null) {
                // the first rule bound for the method didn't match for some other reason than its class version:
                if (version == shared.FirstVersion) {
                    return null;
                }

                return BindSharedRule<T>(shared, args);
            }

            // The shared rule is handed out for a version it does not hold yet, and only then: a call that comes here although
            // the rule holds its version didn't match the rule (its arguments are of other types, say) and gets a rule of its own.
            if (!shared.Versions.TryAdd(version)) {
                return null;
            }

            return (T)sharedRule;
        }

        // RubyMetaBinder.IsForeignMetaObject of the target: a dynamic object of another language is bound by its own meta-object
        internal static bool IsForeignObject(object target) {
            return target is IDynamicMetaObjectProvider && !(target is IRubyDynamicMetaObjectProvider) ||
                target != null && TypeUtils.IsComObjectType(target.GetType());
        }

        // MetaObjectBuilder.AddTargetTypeTest tests a CLR object's type and its class version - and nothing else - in this case:
        internal static bool HasPlainClrTypeTest(RubyClass/*!*/ targetClass, string/*!*/ methodName) {
            if (targetClass.IsSingletonClass || targetClass.GetNonSingletonClass().IsRubyClass) {
                return false;
            }
            var nominalClass = targetClass.NominalClass;
            var singletonMethods = nominalClass.ClrSingletonMethods;
            return !nominalClass.HasAdoptedInstances && (singletonMethods == null ||
                !singletonMethods.ContainsKey(methodName) && !singletonMethods.ContainsKey(Symbols.MethodMissing));
        }

        private T/*!*/ BindSharedRule<T>(SharedRule/*!*/ shared, object[]/*!*/ args) where T : class {
            var versionSet = new RuleVersionSet();
            T result;

            _PendingVersionSet = versionSet;
            _PendingVersionSetOwner = this;
            try {
                result = this.LightBind<T>(args, Context.CompilationThreshold);
            } finally {
                _PendingVersionSet = null;
                _PendingVersionSetOwner = null;
            }
            CacheTarget(result);

            // The rule tests the version set iff Build has taken it and bound it to a version the method was resolved for:
            if (versionSet.Member == shared.Member) {
                lock (_SharedRulesLock) {
                    if (shared.Rule == null) {
                        shared.Versions = versionSet;
                        shared.Rule = result;
                    }
                }
            }
            return result;
        }

        #endregion

        private static bool RequiresUnusedBlockCheck(RubyMemberInfo/*!*/ method) {
            var rubyMethod = method as RubyMethodInfo;
            return rubyMethod != null && !rubyMethod.UsesBlock;
        }

        protected override bool Build(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, bool defaultFallback) {
            if (_PendingVersionSetOwner == this) {
                metaBuilder.TargetVersionSet = _PendingVersionSet;
                _PendingVersionSet = null;
                _PendingVersionSetOwner = null;
            }
            return BuildCall(metaBuilder, _methodName, args, defaultFallback, true);
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildCall(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args, 
            bool defaultFallback, bool callClrMethods) {

            RubyMemberInfo methodMissing;
            var method = Resolve(metaBuilder, methodName, args, out methodMissing);

            if (method.Found) {
                if (!callClrMethods && !method.Info.IsRubyMember) {
                    return false;
                }

                if (args.Signature.ResolveOnly) {
                    metaBuilder.Result = AstFactory.True;
                    return true;    
                }
                
                if (args.Signature.IsVirtualCall && !method.Info.IsRubyMember) {
                    metaBuilder.Result = Ast.Field(null, Fields.ForwardToBase);
                    return true;
                }

                if (DispatchIntrinsics.TryBuildRespondTo(metaBuilder, args, method.Info)) {
                    return true;
                }

                string sentName;
                if (defaultFallback && callClrMethods && DispatchIntrinsics.TryBuildSend(metaBuilder, args, method.Info, out sentName)) {
                    return BuildCall(metaBuilder, sentName, args, defaultFallback, callClrMethods);
                }

                method.Info.BuildCall(metaBuilder, args, methodName);

                // MRI 3.4+: a block passed to a method that never uses it may be ignored
                if (args.Signature.HasSourceBlock && RequiresUnusedBlockCheck(method.Info) && !metaBuilder.Error && metaBuilder.Result != null) {
                    metaBuilder.Result = Ast.Block(
                        Methods.WarnUnusedBlock.OpCall(
                            AstUtils.Convert(args.GetBlockExpression(), typeof(Proc)),
                            AstUtils.Constant(method.Info, typeof(RubyMethodInfo))
                        ),
                        metaBuilder.Result
                    );
                }
                return true;
            } else if (args.Signature.ResolveOnly) {
                if (!args.Signature.HasImplicitSelf && method.IncompatibleVisibility == RubyMethodVisibility.None) {
                    // defined?(obj.m) for an obj with no m at all: MRI asks respond_to_missing?(:m, false)
                    metaBuilder.Result = Methods.IsTrue.OpCall(AstUtils.LightDynamic(
                        RubyCallAction.Make(args.RubyContext, Symbols.RespondToMissing, RubyCallSignature.WithImplicitSelf(2)),
                        args.TargetExpression,
                        Ast.Constant(args.RubyContext.CreateSymbol(methodName, RubyEncoding.Binary)),
                        AstUtils.Constant(false, typeof(object))
                    ));
                } else {
                    metaBuilder.Result = AstFactory.False;
                }
                return true;
            } else {
                return BuildMethodMissingCall(metaBuilder, args, methodName, methodMissing, method.IncompatibleVisibility, false, defaultFallback);
            }
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildAccess(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args, 
            bool defaultFallback, bool callClrMethods) {

            RubyMemberInfo methodMissing;
            var method = Resolve(metaBuilder, methodName, args, out methodMissing);

            if (method.Found) {
                if (!callClrMethods && !method.Info.IsRubyMember) {
                    return false;
                }

                if (method.Info.IsDataMember) {
                    method.Info.BuildCall(metaBuilder, args, methodName);
                } else {
                    metaBuilder.Result = Methods.CreateBoundMember.OpCall(
                        AstUtils.Convert(args.TargetExpression, typeof(object)),
                        Ast.Constant(method.Info, typeof(RubyMemberInfo)),
                        Ast.Constant(methodName)
                    );
                }
                return true;
            } else {
                // Ruby doesn't have "attribute_missing" so we will always use method_missing and return a bound method object:
                return BuildMethodMissingAccess(metaBuilder, args, methodName, methodMissing, method.IncompatibleVisibility, false, defaultFallback);
            }
        }

        private static VisibilityContext GetVisibilityContext(RubyCallSignature callSignature, RubyScope scope) {
            // A call that can only see public members says so whatever else it carries. It may
            // still need the scope - Kernel#public_send honours the refinements active where it
            // was called - and asking the scope who self is would make a protected method visible
            // to a receiver of the same class, which is exactly what public_send must not do.
            if (callSignature.IsInteropCall) {
                return new VisibilityContext(RubyMethodAttributes.Public);
            }

            return callSignature.HasImplicitSelf || !callSignature.HasScope ? 
                new VisibilityContext(RubyMethodAttributes.VisibilityMask) :
                new VisibilityContext(scope.SelfImmediateClass);
        }

        internal static MethodResolutionResult Resolve(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args,
            out RubyMemberInfo methodMissing) {

            MethodResolutionResult method;
            var targetClass = args.TargetClass;
            var visibilityContext = GetVisibilityContext(args.Signature, args.Scope);
            RefinementActivation refinements = null;
            using (targetClass.Context.ClassHierarchyLocker()) {
                var versionSet = metaBuilder.TargetVersionSet;
                metaBuilder.TargetVersionSet = null;

                metaBuilder.AddTargetTypeTest(args.Target, targetClass, args.TargetExpression, args.MetaContext, 
                    new[] { methodName, Symbols.MethodMissing }, versionSet
                );

                // Refinements are lexically scoped, so resolution has to consult the scope the call site was
                // compiled in - which IronRuby hands to every source-level call site as argument 0.  Sites
                // whose target class has never been refined are built exactly as before and pay nothing.
                if (args.Signature.HasScope && !args.Signature.IsSuperCall && targetClass.Context.HasRefinements
                    && targetClass.Context.IsRefinementSensitive(targetClass)) {
                    refinements = args.Scope.GetActiveRefinements();
                }

                if (args.Signature.IsSuperCall) {
                    Debug.Assert(!args.Signature.IsVirtualCall && args.Signature.HasImplicitSelf);
                    method = targetClass.ResolveSuperMethodNoLock(methodName, targetClass).InvalidateSitesOnOverride();
                } else {
                    var options = args.Signature.IsVirtualCall ? MethodLookup.Virtual : MethodLookup.Default;
                    method = targetClass.ResolveMethodNoLock(methodName, visibilityContext, options, refinements).InvalidateSitesOnOverride();
                }

                if (!method.Found) {
                    methodMissing = targetClass.ResolveMethodMissingForSite(methodName, method.IncompatibleVisibility);
                } else {
                    methodMissing = null;
                }

                // the version the test was bound against resolves to this method:
                if (versionSet != null && versionSet.Count > 0) {
                    versionSet.Member = method.Found ? method.Info : null;
                }
            }

            // The rule is only valid while the caller's lexical activation is the one it was bound against.
            // RefinementActivation instances are canonical per lexical position, so reference equality is
            // enough; a `using' anywhere bumps RubyContext.RefinementVersion, every scope recomputes a fresh
            // table and this condition fails, forcing a re-bind.
            if (refinements != null) {
                metaBuilder.AddCondition(Ast.Equal(
                    Methods.GetActiveRefinements.OpCall(AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope))),
                    Ast.Constant(refinements, typeof(RefinementActivation))
                ));
            }

            // Whenever the current self's class changes we need to invalidate the rule, if a protected method is being called.
            if (method.Info != null && method.Info.IsProtected && visibilityContext.Class != null) {
                // We don't need to compare versions, just the class objects (super-class relationship cannot be changed).
                // Since we don't want to hold on a class object (to make it collectible) we compare references to the version handlers.
                metaBuilder.AddCondition(Ast.Equal(
                    Methods.GetSelfClassVersionHandle.OpCall(AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope))),
                    Ast.Constant(visibilityContext.Class.Version)
                ));
            }

            return method;
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildMethodMissingCall(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ methodName,
            RubyMemberInfo methodMissing, RubyMethodVisibility incompatibleVisibility, bool isSuperCall, bool defaultFallback) {

            switch (BindToKernelMethodMissing(metaBuilder, args, methodName, methodMissing, incompatibleVisibility, isSuperCall)) {
                case MethodMissingBinding.Custom:
                    Debug.Assert(!metaBuilder.Error);
                    methodMissing.BuildMethodMissingCall(metaBuilder, args, methodName);
                    return true;

                case MethodMissingBinding.Error:
                    // method_missing is defined in Kernel, error has been reported:
                    return true;

                case MethodMissingBinding.Fallback:
                    // method_missing is defined in Kernel:
                    if (defaultFallback && args.Signature.IsVariableCall) {
                        metaBuilder.SetError(Methods.MakeUndefinedLocalOrMethodError.OpCall(
                            args.MetaContext.Expression,
                            AstUtils.Convert(args.TargetExpression, typeof(object)),
                            Ast.Constant(methodName)
                        ));
                        return true;
                    }
                    if (defaultFallback) {
                        metaBuilder.SetError(WithCallArguments(Methods.MakeMissingMethodError.OpCall(
                            args.MetaContext.Expression,
                            AstUtils.Convert(args.TargetExpression, typeof(object)),
                            Ast.Constant(methodName)
                        ), args));
                        return true;
                    }
                    return false;
            }
            throw Assert.Unreachable;
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildMethodMissingAccess(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ methodName,
            RubyMemberInfo methodMissing, RubyMethodVisibility incompatibleVisibility, bool isSuperCall, bool defaultFallback) {

            switch (BindToKernelMethodMissing(metaBuilder, args, methodName, methodMissing, incompatibleVisibility, isSuperCall)) {
                case MethodMissingBinding.Custom:
                    // we pretend we found the member and return a method that calls method_missing:
                    Debug.Assert(!metaBuilder.Error);
                    metaBuilder.Result = Methods.CreateBoundMissingMember.OpCall(
                        AstUtils.Convert(args.TargetExpression, typeof(object)),
                        Ast.Constant(methodMissing, typeof(RubyMemberInfo)),
                        Ast.Constant(methodName)
                    );
                    return true;

                case MethodMissingBinding.Error:
                    // method_missing is defined in Kernel, error has been reported:
                    return true;

                case MethodMissingBinding.Fallback:
                    // method_missing is defined in Kernel:
                    if (defaultFallback) {
                        metaBuilder.SetError(Methods.MakeMissingMemberError.OpCall(Ast.Constant(methodName)));
                        return true;
                    }
                    return false;
            }
            throw Assert.Unreachable;
        }

        // NoMethodError#args: MRI records the arguments of the call that found no (callable) method.
        private static Expression/*!*/ WithCallArguments(Expression/*!*/ error, CallArguments/*!*/ args) {
            var simple = new List<Expression>();
            foreach (var arg in args.GetSimpleArgumentExpressions()) {
                simple.Add(AstUtils.Box(arg));
            }

            var splat = args.GetSplattedArgumentExpression();
            var rhs = args.GetRhsArgumentExpression();

            return Methods.SetMissingMethodArguments.OpCall(
                error,
                Ast.NewArrayInit(typeof(object), simple),
                splat != null ? AstUtils.Convert(splat, typeof(IList)) : (Expression)AstUtils.Constant(null, typeof(IList)),
                rhs != null ? AstUtils.Box(rhs) : AstUtils.Constant(null),
                AstUtils.Constant(rhs != null)
            );
        }

        private enum MethodMissingBinding {
            Error,
            Fallback,
            Custom
        }

        private static MethodMissingBinding BindToKernelMethodMissing(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ methodName,
            RubyMemberInfo methodMissing, RubyMethodVisibility incompatibleVisibility, bool isSuperCall) {

            // TODO: better specialization of method_missing methods
            if (methodMissing == null ||
                methodMissing.DeclaringModule == methodMissing.Context.BasicObjectClass && methodMissing is RubyLibraryMethodInfo) {

                if (isSuperCall) {
                    metaBuilder.SetError(Methods.MakeMissingSuperException.OpCall(AstUtils.Constant(methodName)));
                } else if (incompatibleVisibility == RubyMethodVisibility.Private) {
                    metaBuilder.SetError(WithCallArguments(Methods.MakePrivateMethodCalledError.OpCall(
                        AstUtils.Convert(args.MetaContext.Expression, typeof(RubyContext)), args.TargetExpression, AstUtils.Constant(methodName)), args)
                    );
                } else if (incompatibleVisibility == RubyMethodVisibility.Protected) {
                    metaBuilder.SetError(WithCallArguments(Methods.MakeProtectedMethodCalledError.OpCall(
                        AstUtils.Convert(args.MetaContext.Expression, typeof(RubyContext)), args.TargetExpression, AstUtils.Constant(methodName)), args)
                    );
                } else {
                    return MethodMissingBinding.Fallback;
                }

                return MethodMissingBinding.Error;
            }

            return MethodMissingBinding.Custom;
        }

        protected override DynamicMetaObjectBinder GetInteropBinder(RubyContext/*!*/ context, IList<DynamicMetaObject/*!*/>/*!*/ args,
            out MethodInfo postConverter) {

            postConverter = null;
                    
            ExpressionType op;
            int opArity = RubyUtils.TryMapOperator(_methodName, out op);
            if (opArity == 1 + args.Count) {
                switch (opArity) {
                    case 1: return context.MetaBinderFactory.InteropUnaryOperation(op);
                    case 2: return context.MetaBinderFactory.InteropBinaryOperation(op);
                }
            }
       
            switch (_methodName) {
                case "new":
                    return context.MetaBinderFactory.InteropCreateInstance(new CallInfo(args.Count));

                case "call":
                    return context.MetaBinderFactory.InteropInvoke(new CallInfo(args.Count));

                case "to_s":
                    if (args.Count == 0) {
                        postConverter = Methods.ObjectToMutableString;
                        return context.MetaBinderFactory.InteropInvokeMember("ToString", new CallInfo(0));
                    }
                    goto default;

                case "to_str":
                    if (args.Count == 0) {
                        postConverter = Methods.StringToMutableString;
                        return context.MetaBinderFactory.InteropConvert(typeof(string), false);
                    }
                    goto default;

                case "[]":
                    // TODO: or invoke?
                    return context.MetaBinderFactory.InteropGetIndex(new CallInfo(args.Count));

                case "[]=":
                    return context.MetaBinderFactory.InteropSetIndex(new CallInfo(args.Count));

                default:
                    if (_methodName.LastCharacter() == '=') {
                        var baseName = _methodName.Substring(0, _methodName.Length - 1);
                        if (args.Count == 1) {
                            return context.MetaBinderFactory.InteropSetMember(baseName);
                        } else {
                            return context.MetaBinderFactory.InteropSetIndexedProperty(baseName, new CallInfo(args.Count));
                        }
                    } else {
                        return context.MetaBinderFactory.InteropInvokeMember(_methodName, new CallInfo(args.Count));
                    }
            }
        }
    }
}

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
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Dynamic;

using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Actions.Calls;

using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Generation;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Calls {
    using Ast = Expression;

    public sealed class SuperCallAction : RubyMetaBinder {
        private readonly RubyCallSignature _signature;
        private readonly int _lexicalScopeId;

        internal SuperCallAction(RubyContext context, RubyCallSignature signature, int lexicalScopeId)
            : base(context) {
            Debug.Assert(signature.HasImplicitSelf && signature.HasScope && (signature.HasBlock || signature.ResolveOnly));
            _signature = signature;
            _lexicalScopeId = lexicalScopeId;
        }

        public static SuperCallAction/*!*/ Make(RubyContext/*!*/ context, RubyCallSignature signature, int lexicalScopeId) {
            ContractUtils.RequiresNotNull(context, "context");
            return context.MetaBinderFactory.SuperCall(lexicalScopeId, signature);
        }

        [Emitted]
        public static SuperCallAction/*!*/ MakeShared(RubyCallSignature signature, int lexicalScopeId) {
            return RubyMetaBinderFactory.Shared.SuperCall(lexicalScopeId, signature);
        }

        public override string/*!*/ ToString() {
            return "super" + _signature.ToString() + ":" + _lexicalScopeId + (Context != null ? " @" + Context.RuntimeId.ToString() : null);
        }

        public override RubyCallSignature Signature {
            get { return _signature; }
        }

        public override Type/*!*/ ReturnType {
            get { return _signature.ResolveOnly ? typeof(bool) : typeof(object); }
        }

        #region Rule Generation

        protected override bool Build(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, bool defaultFallback) {
            RubyModule currentDeclaringModule;
            string currentMethodName;

            var scope = args.Scope;
            var scopeExpr = AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope));

            RubyScope targetScope;
            int scopeNesting = scope.GetSuperCallTarget(out currentDeclaringModule, out currentMethodName, out targetScope);

            if (scopeNesting == -1) {
                metaBuilder.AddCondition(Methods.IsSuperOutOfMethodScope.OpCall(scopeExpr));
                metaBuilder.SetError(Methods.MakeTopLevelSuperException.OpCall());
                return true;
            }

            object target = targetScope.SelfObject;
            var targetExpression = metaBuilder.GetTemporary(typeof(object), "#super-self");
            var assignTarget = Ast.Assign(
                targetExpression,
                Methods.GetSuperCallTarget.OpCall(scopeExpr, AstUtils.Constant(scopeNesting))
            );

            if (_signature.HasImplicitArguments && targetScope.Kind == ScopeKind.BlockMethod) {
                metaBuilder.AddCondition(Ast.NotEqual(assignTarget, Ast.Field(null, Fields.NeedsUpdate)));
                metaBuilder.SetError(Methods.MakeImplicitSuperInBlockMethodError.OpCall());
                return true;
            }

            // If we need to update we return RubyOps.NeedsUpdate instance that will cause the subsequent conditions to fail:
            metaBuilder.AddInitialization(assignTarget);

            args.SetTarget(targetExpression, target);

            Debug.Assert(currentDeclaringModule != null);

            RubyMemberInfo method;
            RubyMemberInfo methodMissing = null;

            // MRI bug: Uses currentDeclaringModule for method look-up so we can end up with an instance method of class C 
            // called on a target of another class. See http://redmine.ruby-lang.org/issues/show/2419.

            // we need to lock the hierarchy of the target class:
            var targetClass = scope.RubyContext.GetImmediateClassOf(target);
            using (targetClass.Context.ClassHierarchyLocker()) {
                // initialize all methods in ancestors:                
                targetClass.InitializeMethodsNoLock();

                // target is stored in a local, therefore it cannot be part of the restrictions:
                metaBuilder.TreatRestrictionsAsConditions = true;
                metaBuilder.AddTargetTypeTest(target, targetClass, targetExpression, args.MetaContext, 
                    new[] { Symbols.MethodMissing } // currentMethodName is resolved for super, which cannot be an instance singleton
                );
                metaBuilder.TreatRestrictionsAsConditions = false;

                method = targetClass.ResolveSuperMethodNoLock(currentMethodName, currentDeclaringModule).InvalidateSitesOnOverride().Info;

                // A module's method bound (UnboundMethod#bind) to an object that does not include the
                // module: MRI runs it as if the module sat just above the object's class, so super
                // looks the name up on that class. (A refinement is not an ancestor either, but its
                // super is resolved from the refined module.)
                if (method == null && !currentDeclaringModule.IsClass && currentDeclaringModule.RefinedModule == null
                    && !targetClass.HasAncestorNoLock(currentDeclaringModule)) {
                    var candidate = targetClass.ResolveMethodNoLock(currentMethodName, VisibilityContext.AllVisible).InvalidateSitesOnOverride().Info;

                    // A compiled method body is shared by every `def' of it and remembers the module of
                    // the first, so the running body can report a module the receiver never included
                    // (a `def' re-run in Module.new). Landing on that same body again would recurse.
                    RubyMemberInfo current;
                    bool skipHidden = false;
                    if (candidate != null && !(currentDeclaringModule.TryGetMethod(currentMethodName, ref skipHidden, out current)
                        && current != null && candidate.IsEquivalentTo(current))) {
                        method = candidate;
                    }
                }

                if (_signature.ResolveOnly) {
                    metaBuilder.Result = AstUtils.Constant(method != null);
                    return true;
                }

                if (method == null) {
                    // MRI: method_missing is called for the targetClass, not for the super:
                    methodMissing = targetClass.ResolveMethodMissingForSite(currentMethodName, RubyMethodVisibility.None);
                }
            }

            if (method != null) {
                if (currentMethodName == Symbols.Initialize && method is RubyLibraryMethodInfo
                    && (method.DeclaringModule.IsObjectClass || method.DeclaringModule.IsBasicObjectClass)
                    && TryBuildClrConstructorCall(metaBuilder, args, target)) {
                    metaBuilder.BuildControlFlow(args);
                    return true;
                }
                method.BuildSuperCall(metaBuilder, args, currentMethodName, currentDeclaringModule);
            } else {
                return RubyCallAction.BuildMethodMissingCall(metaBuilder, args, currentMethodName, methodMissing, RubyMethodVisibility.None, true, defaultFallback);
            }

            return true;
        }

        #endregion

        /// <summary>
        /// super in initialize of a Ruby class derived from a CLR class, which would otherwise reach Object#initialize:
        /// for such a class the CLR base class's constructor is what constructs the object, as a C# derived class's
        /// constructor calls base(...). The constructor matching the arguments runs on self, which new has allocated.
        /// </summary>
        private static bool TryBuildClrConstructorCall(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, object target) {
            Type clrBase = (target != null) ? RubyClass.GetClrBaseType(target.GetType()) : null;

            // A Ruby builtin backed by a CLR type (Proc, Array, Hash) keeps Ruby's initialize chain:
            if (clrBase == null || (args.RubyContext.GetModule(clrBase).Restrictions & ModuleRestrictions.NoNameMapping) != 0) {
                return false;
            }

            bool privateBinding = args.RubyContext.DomainManager.Configuration.PrivateBinding;
            var constructors = new List<OverloadInfo>();
            foreach (var ctor in clrBase.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                if (ctor.IsPublic || ctor.IsFamily || ctor.IsFamilyOrAssembly || privateBinding) {
                    constructors.Add(new ReflectionOverloadInfo(ctor));
                }
            }
            if (constructors.Count == 0) {
                return false;
            }

            var instance = args.TargetExpression;
            RubyMethodGroupBase.BuildCallNoFlow(metaBuilder, args, Symbols.Initialize, constructors, SelfCallConvention.NoSelf, true);
            if (!metaBuilder.Error) {
                bool rewritten = false;
                var result = InvokeOnInstance(metaBuilder.Result, clrBase, instance, ref rewritten);
                if (!rewritten) {
                    throw new InvalidOperationException("unexpected shape of a constructor call");
                }
                metaBuilder.Result = result;
            }
            return true;
        }

        // Replaces new Base(args) in the bound call - which may be wrapped in conversions, or in the block that sets the
        // members keywords name - with the constructor run on the existing instance.
        private static Expression/*!*/ InvokeOnInstance(Expression/*!*/ expression, Type/*!*/ clrBase, Expression/*!*/ instance, ref bool rewritten) {
            switch (expression.NodeType) {
                case ExpressionType.New:
                    var node = (NewExpression)expression;
                    if (node.Constructor == null || node.Constructor.DeclaringType != clrBase) {
                        return expression;
                    }
                    rewritten = true;
                    var arguments = new Expression[node.Arguments.Count];
                    for (int i = 0; i < arguments.Length; i++) {
                        arguments[i] = AstUtils.Convert(node.Arguments[i], typeof(object));
                    }
                    return Ast.Convert(
                        Ast.Call(RubyOps.InvokeClrConstructorMethod, AstUtils.Constant(node.Constructor, typeof(ConstructorInfo)),
                            AstUtils.Convert(instance, typeof(object)), Ast.NewArrayInit(typeof(object), arguments)),
                        node.Type
                    );

                case ExpressionType.Call:
                    // a constructor not visible to emitted code (a protected one) is called through reflection:
                    var call = (MethodCallExpression)expression;
                    var constructor = (call.Method.DeclaringType == typeof(BinderOps) && call.Method.Name == nameof(BinderOps.InvokeConstructor)) ?
                        call.Arguments[0] as ConstantExpression : null;
                    if (constructor == null || !(constructor.Value is ConstructorInfo) || ((ConstructorInfo)constructor.Value).DeclaringType != clrBase) {
                        return expression;
                    }
                    rewritten = true;
                    return AstUtils.Convert(
                        Ast.Call(RubyOps.InvokeClrConstructorMethod, AstUtils.Constant(constructor.Value, typeof(ConstructorInfo)),
                            AstUtils.Convert(instance, typeof(object)), call.Arguments[1]),
                        call.Type
                    );

                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                    var unary = (UnaryExpression)expression;
                    return unary.Update(InvokeOnInstance(unary.Operand, clrBase, instance, ref rewritten));

                case ExpressionType.Assign:
                    var assign = (BinaryExpression)expression;
                    return assign.Update(assign.Left, assign.Conversion, InvokeOnInstance(assign.Right, clrBase, instance, ref rewritten));

                case ExpressionType.Block:
                    var block = (BlockExpression)expression;
                    var expressions = new Expression[block.Expressions.Count];
                    for (int i = 0; i < expressions.Length; i++) {
                        expressions[i] = InvokeOnInstance(block.Expressions[i], clrBase, instance, ref rewritten);
                    }
                    return block.Update(block.Variables, expressions);

                default:
                    return expression;
            }
        }

        public override Expression/*!*/ CreateExpression() {
            return Methods.GetMethod(GetType(), "MakeShared").OpCall(_signature.CreateExpression(), AstUtils.Constant(_lexicalScopeId));
        }
    }
}

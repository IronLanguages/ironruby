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
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Generation;
using System.Numerics;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime.Conversions {
    using Ast = Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;

    public abstract class RubyConversionAction : RubyMetaBinder {
        protected RubyConversionAction() 
            : base(null) {
        }

        protected RubyConversionAction(RubyContext context)
            : base(context) {
        }

        // All protocol conversion actions ignore the current scope and use RubyContext only. 
        // That means if the to_xxx conversion methods or respond_to? are aliased to scope manipulating methods (e.g. eval, private, ...)
        // it won't work. We assume that this is acceptable (it could be easily changed, we can define 2 kinds of protocol actions: with scope and with context).
        public override RubyCallSignature Signature {
            get { return RubyCallSignature.WithImplicitSelf(0); }
        }

        public override string/*!*/ ToString() {
            return GetType().Name + (Context != null ? " @" + Context.RuntimeId.ToString() : null);
        }

        public static RubyConversionAction TryGetDefaultConversionAction(RubyContext context, Type/*!*/ parameterType) {
            var factory = TryGetDefaultConversionAction(parameterType);
            return factory != null ? factory(context != null ? context.MetaBinderFactory : RubyMetaBinderFactory.Shared) : null;
        }

        public static RubyConversionAction/*!*/ GetConversionAction(RubyContext context, Type/*!*/ parameterType, bool allowProtocolConversions) {
            var factory = TryGetConversionAction(parameterType, allowProtocolConversions);
            return factory != null ? factory(context != null ? context.MetaBinderFactory : RubyMetaBinderFactory.Shared) : null;
        }

        public static bool HasDefaultConversion(Type/*!*/ parameterType) {
            return TryGetDefaultConversionAction(parameterType) != null;
        }

        private static Func<RubyMetaBinderFactory, RubyConversionAction> TryGetConversionAction(Type/*!*/ parameterType, bool allowProtocolConversion) {
            if (allowProtocolConversion) {
                var result = TryGetDefaultConversionAction(parameterType);
                if (result != null) {
                    return result;
                }
            }

            return (factory) => factory.GenericConversionAction(parameterType);
        }

        private static Func<RubyMetaBinderFactory, RubyConversionAction> TryGetDefaultConversionAction(Type/*!*/ parameterType) {
            // TODO: 
            // nullable int (see Array#fill, Sockets:ConvertToSocketFlag, Kernel#open(perm=nil), File.chown, IO#read)

            // TODO: do we want to use a default protocol for enums?
            if (parameterType.IsEnum) {
                return null;
            }

            switch (parameterType.GetTypeCode()) {
                case TypeCode.SByte: return (factory) => factory.Conversion<ConvertToSByteAction>();
                case TypeCode.Byte: return (factory) => factory.Conversion<ConvertToByteAction>();
                case TypeCode.Int16: return (factory) => factory.Conversion<ConvertToInt16Action>();
                case TypeCode.UInt16: return (factory) => factory.Conversion<ConvertToUInt16Action>();
                case TypeCode.Int32: return (factory) => factory.Conversion<ConvertToFixnumAction>();
                case TypeCode.UInt32: return (factory) => factory.Conversion<ConvertToUInt32Action>();
                case TypeCode.Int64: return (factory) => factory.Conversion<ConvertToInt64Action>();
                case TypeCode.UInt64: return (factory) => factory.Conversion<ConvertToUInt64Action>();
                case TypeCode.Single: return (factory) => factory.Conversion<ConvertToSingleAction>();
                case TypeCode.Double: return (factory) => factory.Conversion<ConvertToFAction>();
                case TypeCode.String: return (factory) => factory.Conversion<ConvertToSymbolAction>();
            }

            if (parameterType == typeof(MutableString)) {
                return (factory) => factory.Conversion<ConvertToStrAction>();
            }

            if (parameterType == typeof(BigInteger)) {
                return (factory) => factory.Conversion<ConvertToBignumAction>();
            }

            if (parameterType == typeof(IntegerValue)) {
                return (factory) => factory.Conversion<ConvertToIntAction>();
            }

            if (parameterType == typeof(Union<int, MutableString>)) {
                return (factory) => factory.CompositeConversion(CompositeConversion.ToFixnumToStr);
            }

            if (parameterType == typeof(Union<MutableString, int>)) {
                return (factory) => factory.CompositeConversion(CompositeConversion.ToStrToFixnum);
            }

            if (parameterType == typeof(RubyRegex)) {
                return (factory) => factory.Conversion<ConvertToRegexAction>();
            }

            if (parameterType == typeof(IList)) {
                return (factory) => factory.Conversion<ConvertToArrayAction>();
            }

            if (parameterType == typeof(IDictionary<object, object>)) {
                return (factory) => factory.Conversion<ConvertToHashAction>();
            }

            if (parameterType == typeof(int?)) {
                return (factory) => factory.Conversion<TryConvertToFixnumAction>();
            }

            return null;
        }

        internal static Expression ImplicitConvert(Type/*!*/ toType, CallArguments/*!*/ args) {
            return Converter.ImplicitConvert(args.TargetExpression, CompilerHelpers.GetType(args.Target), toType);
        }

        internal static Expression ExplicitConvert(Type/*!*/ toType, CallArguments/*!*/ args) {
            return Converter.ExplicitConvert(args.TargetExpression, CompilerHelpers.GetType(args.Target), toType);
        }

        internal static Expression Convert(Type/*!*/ toType, CallArguments/*!*/ args) {
            var fromType = CompilerHelpers.GetType(args.Target);
            return Converter.ImplicitConvert(args.TargetExpression, fromType, toType)
                ?? Converter.ExplicitConvert(args.TargetExpression, fromType, toType);
        }
    }

    // Conversion operations:
    // 1) tries implicit conversions
    // 2) calls respond_to? :to_xxx
    // 3) calls to_xxx
    public abstract class ProtocolConversionAction : RubyConversionAction {
        protected ProtocolConversionAction() {
        }

        protected abstract string/*!*/ ToMethodName { get; }
        protected abstract MethodInfo ConversionResultValidator { get; }
        protected abstract string/*!*/ TargetTypeName { get; }

        /// <summary>
        /// MRI words an IMPLICIT protocol conversion failure ("no implicit conversion of X into Y")
        /// differently from an EXPLICIT one (Kernel#Integer / Kernel#Float: "can't convert X into Y").
        /// Almost every conversion here is implicit; the Integer()/Float() ones override this.
        /// </summary>
        protected virtual MethodInfo/*!*/ ConversionErrorFactory {
            get { return Methods.CreateImplicitConversionError; }
        }

        public override Type/*!*/ ReturnType {
            get { 
                return (ConversionResultValidator != null) ? ConversionResultValidator.ReturnType : typeof(object); 
            }
        }

        protected override bool Build(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, bool defaultFallback) {
            Debug.Assert(defaultFallback, "custom fallback not supported");
            BuildConversion(metaBuilder, args, ReturnType, this);
            return true;
        }

        internal static void BuildConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, Type/*!*/ resultType,
            params ProtocolConversionAction/*!*/[]/*!*/ conversions) {
            Assert.NotNull(metaBuilder, args, conversions);
            Debug.Assert(args.SimpleArgumentCount == 0 && !args.Signature.HasBlock && !args.Signature.HasSplattedArgument && !args.Signature.HasRhsArgument);
            Debug.Assert(!args.Signature.HasScope);

            // implicit conversions should only depend on the static type:
            foreach (var conversion in conversions) {
                if (conversion.TryImplicitConversion(metaBuilder, args)) {
                    metaBuilder.AddObjectTypeRestriction(args.Target, args.TargetExpression);

                    if (!metaBuilder.Error) {
                        metaBuilder.Result = ConvertResult(metaBuilder.Result, resultType);
                    }
                    return;
                }
            }

            RubyClass targetClass = args.RubyContext.GetImmediateClassOf(args.Target);
            // Ruby names true and false themselves rather than their classes in conversion errors:
            // "can't convert true into Integer", not "can't convert TrueClass into Integer". The
            // site is keyed on the class, so reading the value here is still cacheable.
            // GetNonNullName rather than Name: an anonymous class has no name, and MRI
            // still prints something there.
            string targetClassName = (args.Target is bool)
                ? ((bool)args.Target ? "true" : "false")
                : targetClass.GetNonSingletonClass().GetNonNullName(args.RubyContext);
            Expression targetClassNameConstant = AstUtils.Constant(targetClassName, typeof(string));
            MethodResolutionResult respondToMethod, methodMissing = MethodResolutionResult.NotFound;
            ProtocolConversionAction selectedConversion = null;
            RubyMemberInfo conversionMethod = null;
            bool customMethodMissing = false, customRespondToMissing = false;

            using (targetClass.Context.ClassHierarchyLocker()) {
                // check for type version:
                metaBuilder.AddTargetTypeTest(args.Target, targetClass, args.TargetExpression, args.MetaContext,
                    ArrayUtils.Insert(Symbols.RespondTo, Symbols.MethodMissing,
                        ArrayUtils.Insert(Symbols.RespondToMissing, ArrayUtils.ConvertAll(conversions, (c) => c.ToMethodName)))
                );

                // we can optimize if Kernel#respond_to? method is not overridden, and we must take the same
                // path when respond_to? is missing altogether (BasicObject does not include Kernel) - otherwise
                // selectedConversion stays null and the error path below dereferences it.
                respondToMethod = targetClass.ResolveMethodForSiteNoLock(Symbols.RespondTo, VisibilityContext.AllVisible);
                if (!respondToMethod.Found ||
                    (respondToMethod.Info.DeclaringModule == targetClass.Context.KernelModule && respondToMethod.Info is RubyLibraryMethodInfo)) { // TODO: better override detection
                    if (!respondToMethod.Found) {
                        // a BasicObject that defines respond_to? later has to get the slow path then
                        methodMissing = targetClass.ResolveMethodNoLock(Symbols.MethodMissing, VisibilityContext.AllVisible);
                        methodMissing.InvalidateSitesOnMissingMethodAddition(Symbols.RespondTo, targetClass.Context);
                    }
                    respondToMethod = MethodResolutionResult.NotFound;

                    // get the first applicable conversion:
                    foreach (var conversion in conversions) {
                        selectedConversion = conversion;
                        conversionMethod = targetClass.ResolveMethodForSiteNoLock(conversion.ToMethodName, VisibilityContext.AllVisible).Info;
                        if (conversionMethod != null) {
                            break;
                        } else {
                            // find method_missing - we need to add "to_xxx" methods to the missing methods table:
                            if (!methodMissing.Found) {
                                methodMissing = targetClass.ResolveMethodNoLock(Symbols.MethodMissing, VisibilityContext.AllVisible);
                            }
                            methodMissing.InvalidateSitesOnMissingMethodAddition(conversion.ToMethodName, targetClass.Context);
                        }
                    }

                    if (conversionMethod == null) {
                        // Whether the object gets a second chance through method_missing depends on
                        // what it has overridden, and both answers are known here rather than at
                        // call time - an object that has overridden neither (the overwhelmingly
                        // common case) costs nothing extra.
                        // The method_missing a hosting scope installs on main is there to resolve
                        // names from that scope, not to answer conversions, and it needs a scope
                        // that a conversion site has no way to pass. Asking it for #to_ary is both
                        // meaningless and fatal, so it does not count as overridden here.
                        customMethodMissing = IsOverridden(methodMissing.Info, targetClass.Context.BasicObjectClass)
                            && !(methodMissing.Info is RubyScopeMethodMissingInfo);
                        customRespondToMissing = IsOverridden(
                            targetClass.ResolveMethodForSiteNoLock(Symbols.RespondToMissing, VisibilityContext.AllVisible).Info,
                            targetClass.Context.KernelModule
                        );
                    }
                }
            }

            if (!respondToMethod.Found) {
                if (conversionMethod == null) {
                    // No to_xxx method - but MRI does not give up there. An object that has
                    // overridden method_missing is asked for the conversion anyway, so that it can
                    // answer one without defining a method for it; an object that has overridden
                    // respond_to_missing? gets to veto that. Only "neither" is the conversion error.
                    metaBuilder.Result = MakeMethodMissingConversion(args, selectedConversion, targetClassNameConstant, resultType,
                        customMethodMissing, customRespondToMissing);
                    return;
                } else {
                    // invoke target.to_xxx() and validate it; returns an instance of TTargetType:
                    conversionMethod.BuildCall(metaBuilder, args, selectedConversion.ToMethodName);

                    if (!metaBuilder.Error) {
                        metaBuilder.Result = ConvertResult(
                            selectedConversion.MakeValidatorCall(args, targetClassNameConstant, metaBuilder.Result), 
                            resultType
                        );
                    }
                    return;
                }
            }

            // slow path: invoke respond_to?, to_xxx and result validation.
            // MRI (vm_respond_to) passes include_all = true unless respond_to? takes only the name.
            bool passIncludeAll = respondToMethod.Info.GetArity() != 1;
            for (int i = conversions.Length - 1; i >= 0; i--) {
                string toMethodName = conversions[i].ToMethodName;
                
                var conversionCallSite = AstUtils.LightDynamic(
                    RubyCallAction.Make(args.RubyContext, toMethodName, RubyCallSignature.WithImplicitSelf(0)),
                    args.TargetExpression
                );

                metaBuilder.Result = Ast.Condition(
                    // If

                    // respond_to?()
                    Methods.IsTrue.OpCall(passIncludeAll
                        ? AstUtils.LightDynamic(
                            RubyCallAction.Make(args.RubyContext, Symbols.RespondTo, RubyCallSignature.WithImplicitSelf(2)),
                            args.TargetExpression, 
                            Ast.Constant(args.RubyContext.CreateSymbol(toMethodName, RubyEncoding.Binary)),
                            AstUtils.Constant(true, typeof(object))
                          )
                        : AstUtils.LightDynamic(
                            RubyCallAction.Make(args.RubyContext, Symbols.RespondTo, RubyCallSignature.WithImplicitSelf(1)),
                            args.TargetExpression, 
                            Ast.Constant(args.RubyContext.CreateSymbol(toMethodName, RubyEncoding.Binary))
                          )
                    ),

                    // Then

                    // to_xxx():
                    ConvertResult(
                        conversions[i].MakeValidatorCall(args, targetClassNameConstant, conversionCallSite),
                        resultType
                    ),

                    // Else

                    (i < conversions.Length - 1) ? metaBuilder.Result : 
                        conversions[i].MakeErrorExpression(args, targetClassNameConstant, resultType)
                );
            }
        }

        /// <summary>
        /// True if the method resolved to something other than the built-in definition it inherits
        /// from <paramref name="builtinOwner"/>, i.e. the object really has overridden it.
        /// </summary>
        private static bool IsOverridden(RubyMemberInfo method, RubyModule builtinOwner) {
            return method != null && !(method.DeclaringModule == builtinOwner && method is RubyLibraryMethodInfo);
        }

        /// <summary>
        /// The to_xxx method the default conversion to <paramref name="parameterType"/> calls (to_str for a String
        /// parameter, to_int for an Int32 one, ...), or null if that conversion is not a single protocol method.
        /// </summary>
        internal static string GetDefaultConversionMethodName(RubyContext/*!*/ context, Type/*!*/ parameterType) {
            var conversion = TryGetDefaultConversionAction(context, parameterType) as ProtocolConversionAction;
            return (conversion != null) ? conversion.ToMethodName : null;
        }

        /// <summary>
        /// Everything whose definition decides whether an object answers a default conversion (see <see cref="CanConvert"/>):
        /// the to_xxx methods the conversions of TryGetDefaultConversionAction call, and the methods asked instead of them.
        /// </summary>
        internal static readonly string/*!*/[]/*!*/ DefaultConversionMethodNames = new[] {
            Symbols.ToStr, Symbols.ToInt, Symbols.ToF, Symbols.ToAry, Symbols.ToHash,
            Symbols.RespondTo, Symbols.MethodMissing, Symbols.RespondToMissing
        };

        /// <summary>
        /// False if <see cref="BuildConversion"/> could only build the conversion error for an object of
        /// <paramref name="targetClass"/>: the class has no <paramref name="methodName"/> and leaves respond_to? and
        /// method_missing as Kernel and BasicObject define them, so nothing asks the object at run time.
        /// The lookups are registered as BuildConversion's are: defining any of these methods later changes the
        /// version of the class, which a rule that relies on the answer has to test.
        /// </summary>
        internal static bool CanConvert(RubyClass/*!*/ targetClass, string/*!*/ methodName) {
            RubyContext context = targetClass.Context;
            context.RequiresClassHierarchyLock();

            var respondTo = targetClass.ResolveMethodForSiteNoLock(Symbols.RespondTo, VisibilityContext.AllVisible);
            if (respondTo.Found && IsOverridden(respondTo.Info, context.KernelModule)) {
                return true;
            }

            var methodMissing = targetClass.ResolveMethodForSiteNoLock(Symbols.MethodMissing, VisibilityContext.AllVisible);
            if (!respondTo.Found) {
                methodMissing.InvalidateSitesOnMissingMethodAddition(Symbols.RespondTo, context);
            }

            if (targetClass.ResolveMethodForSiteNoLock(methodName, VisibilityContext.AllVisible).Found) {
                return true;
            }
            methodMissing.InvalidateSitesOnMissingMethodAddition(methodName, context);

            // An object that has overridden method_missing is asked for the conversion anyway (MakeMethodMissingConversion).
            // One that has overridden only respond_to_missing? is asked that, but nothing could answer the call then:
            return IsOverridden(methodMissing.Info, context.BasicObjectClass) && !(methodMissing.Info is RubyScopeMethodMissingInfo);
        }

        /// <summary>
        /// The conversion for a receiver whose class does not define to_xxx. MRI's rb_check_funcall
        /// asks method_missing for it, unless a user-written respond_to_missing? says no first:
        ///   both overridden:            respond_to_missing?(:to_xxx, true) ? to_xxx : &lt;error&gt;
        ///   only method_missing:        to_xxx                    (the default veto never fires)
        ///   only respond_to_missing?:   ask it for its side effects, then &lt;error&gt;
        ///   neither:                    &lt;error&gt;
        /// </summary>
        private static Expression/*!*/ MakeMethodMissingConversion(CallArguments/*!*/ args, ProtocolConversionAction/*!*/ conversion,
            Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType, bool customMethodMissing, bool customRespondToMissing) {

            var error = conversion.MakeErrorExpression(args, targetClassNameConstant, resultType);
            if (!customMethodMissing && !customRespondToMissing) {
                return error;
            }

            string toMethodName = conversion.ToMethodName;
            Expression respondToMissing = customRespondToMissing
                ? AstUtils.LightDynamic(
                    RubyCallAction.Make(args.RubyContext, Symbols.RespondToMissing, RubyCallSignature.WithImplicitSelf(2)),
                    args.TargetExpression,
                    Ast.Constant(args.RubyContext.CreateSymbol(toMethodName, RubyEncoding.Binary)),
                    AstUtils.Constant(true, typeof(object))
                  )
                : null;

            if (!customMethodMissing) {
                // Nothing would answer the call, but respond_to_missing? is still asked - it is
                // user code and may have side effects that MRI's answer depends on having run.
                return Ast.Block(respondToMissing, error);
            }

            var conversionCallSite = AstUtils.LightDynamic(
                RubyCallAction.Make(args.RubyContext, toMethodName, RubyCallSignature.WithImplicitSelf(0)),
                args.TargetExpression
            );
            var converted = ConvertResult(conversion.MakeValidatorCall(args, targetClassNameConstant, conversionCallSite), resultType);

            if (respondToMissing != null) {
                return Ast.Condition(Methods.IsTrue.OpCall(respondToMissing), converted, error);
            }

            // The object never actually said it could do this - method_missing is simply there.
            // So a NoMethodError coming back out of it means "no, I can't", not an error to report,
            // and the conversion failure is reported instead. That is how a plain method_missing
            // that ends in super still yields "no implicit conversion of Foo into Array".
            return Ast.TryCatch(converted, Ast.Catch(typeof(MissingMethodException), error));
        }

        internal protected abstract bool TryImplicitConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args);

        protected virtual Expression/*!*/ MakeErrorExpression(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            return Ast.Throw(
                ConversionErrorFactory.OpCall(targetClassNameConstant, AstUtils.Constant(TargetTypeName)),
                resultType
            );
        }

        protected virtual void SetError(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            metaBuilder.SetError(ConversionErrorFactory.OpCall(targetClassNameConstant, AstUtils.Constant(TargetTypeName)));
        }

        protected virtual Expression/*!*/ MakeValidatorCall(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Expression/*!*/ result) {
            var validator = ConversionResultValidator;
            return (validator != null) ? validator.OpCall(targetClassNameConstant, AstUtils.Box(result)) : result;
        }

        private static Expression/*!*/ ConvertResult(Expression/*!*/ expression, Type/*!*/ resultType) {
            if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Union<,>)) {
                var args = resultType.GetGenericArguments();
                var ctor = resultType.GetConstructor(args);
                if (args[0].IsAssignableFrom(expression.Type)) {
                    return Ast.New(ctor, expression, AstUtils.Default(args[1]));
                } else {
                    Debug.Assert(args[1].IsAssignableFrom(expression.Type));
                    return Ast.New(ctor, AstUtils.Default(args[0]), expression);
                }
            } else {
                return AstUtils.Convert(expression, resultType);
            }
        }
    }

    public abstract class ProtocolConversionAction<TSelf> : ProtocolConversionAction
        where TSelf : ProtocolConversionAction<TSelf>, new() {
        
        // The action Make handed out last. Library methods ask for their conversion on every
        // call (Protocols.CastToFixnum and friends), and the factory's locked, type-keyed lookup
        // costs far more than the conversion itself. One action per runtime, so a single slot
        // answers every call of a process that has one runtime; a second runtime just misses.
        private static TSelf _lastMade;

        public static TSelf/*!*/ Make(RubyContext/*!*/ context) {
            TSelf last = _lastMade;
            if (last != null && ReferenceEquals(last.Context, context)) {
                return last;
            }
            return _lastMade = context.MetaBinderFactory.Conversion<TSelf>();
        }

        [Emitted]
        public static TSelf/*!*/ MakeShared() {
            return RubyMetaBinderFactory.Shared.Conversion<TSelf>();
        }

        protected ProtocolConversionAction() {
            Debug.Assert(GetType() == typeof(TSelf));
        }

        public override Expression/*!*/ CreateExpression() {
            return Methods.GetMethod(typeof(ProtocolConversionAction<TSelf>), "MakeShared").OpCall();
        }
    }

    public abstract class ConvertToReferenceTypeAction<TSelf, TTargetType> : ProtocolConversionAction<TSelf>
        where TSelf : ConvertToReferenceTypeAction<TSelf, TTargetType>, new()
        where TTargetType : class {

        internal protected override bool TryImplicitConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            return TryImplicitConversionInternal(metaBuilder, args);
        }

        internal static bool TryImplicitConversionInternal(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            if (args.Target == null) {
                metaBuilder.Result = AstUtils.Constant(null, typeof(TTargetType));
                return true;
            }

            var convertedTarget = args.Target as TTargetType;
            if (convertedTarget != null) {
                metaBuilder.Result = AstUtils.Convert(args.TargetExpression, typeof(TTargetType));
                return true;
            }
            return false;
        }
    }

    public abstract class TryConvertToReferenceTypeAction<TSelf, TTargetType> : ConvertToReferenceTypeAction<TSelf, TTargetType>
        where TSelf : TryConvertToReferenceTypeAction<TSelf, TTargetType>, new()
        where TTargetType : class {

        // return null if the object doesn't handle the conversion:
        protected override Expression/*!*/ MakeErrorExpression(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            return AstUtils.Constant(null, resultType);
        }

        // return null if the object doesn't handle the conversion:
        protected override void SetError(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            metaBuilder.Result = AstUtils.Constant(null, resultType);
        }
    }

    public sealed class ConvertToProcAction : ConvertToReferenceTypeAction<ConvertToProcAction, Proc> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToProc; } }
        protected override string/*!*/ TargetTypeName { get { return "Proc"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToProcValidator; } }
    }

    #region String, Path, Symbol, Regex

    public sealed class ConvertToStrAction : ConvertToReferenceTypeAction<ConvertToStrAction, MutableString> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToStr; } }
        protected override string/*!*/ TargetTypeName { get { return "String"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToStringValidator; } }

        // Ruby 1.8 let a Symbol stand in wherever a String was implicitly wanted; 1.9 removed
        // that, and Symbol has had no #to_str since.  `"a" + :b` is a TypeError, not "ab".
        // There is no special case here for that reason - a Symbol reaches the generic error
        // path and is reported as "no implicit conversion of Symbol into String".

        protected override DynamicMetaObjectBinder/*!*/ GetInteropBinder(RubyContext/*!*/ context, IList<DynamicMetaObject/*!*/>/*!*/ args, 
            out MethodInfo postConverter) {
            postConverter = Methods.StringToMutableString;
            return context.MetaBinderFactory.InteropConvert(typeof(string), true);
        }
    }

    public sealed class ConvertToPathAction : ConvertToReferenceTypeAction<ConvertToPathAction, MutableString> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToPath; } }
        protected override string/*!*/ TargetTypeName { get { return "String"; } }
        protected override MethodInfo ConversionResultValidator { get { return null; } }
        
        protected override Expression/*!*/ MakeValidatorCall(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Expression/*!*/ result) {
            return AstUtils.LightDynamic(ConvertToStrAction.Make(args.RubyContext), AstUtils.Box(result));
        }
    }

    public sealed class TryConvertToPathAction : TryConvertToReferenceTypeAction<TryConvertToPathAction, MutableString> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToPath; } }
        protected override string/*!*/ TargetTypeName { get { return "String"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToStringValidator; } }
    }

    public sealed class TryConvertToStrAction : TryConvertToReferenceTypeAction<TryConvertToStrAction, MutableString> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToStr; } }
        protected override string/*!*/ TargetTypeName { get { return "String"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.TryToStringValidator; } }

        // No Symbol case, for the same reason as ConvertToStrAction: String.try_convert(:a)
        // is nil, not "a".
    }

    // TODO: escaping vs. non-escaping?
    // This conversion escapes the regex. 
    public sealed class ConvertToRegexAction : ConvertToReferenceTypeAction<ConvertToRegexAction, RubyRegex> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToStr; } }
        protected override string/*!*/ TargetTypeName { get { return "Regexp"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToRegexValidator; } }
    }

    // TODO: remove (replace by MutableString/RubySymbol conversion)
    public sealed class ConvertToSymbolAction : ConvertToReferenceTypeAction<ConvertToSymbolAction, string> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToStr; } }
        protected override string/*!*/ TargetTypeName { get { return "Symbol"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToSymbolValidator; } }

        // MRI does not use the generic "no implicit conversion of X into Symbol" wording where a
        // method name was expected; it names the offending value instead. The factory therefore
        // needs the value, not just its class name, which is why both error paths are overridden
        // rather than only ConversionErrorFactory.
        private Expression/*!*/ MakeNotSymbolNorStringError(CallArguments/*!*/ args) {
            return Methods.CreateNotSymbolNorStringError.OpCall(
                AstUtils.Convert(args.MetaContext.Expression, typeof(RubyContext)),
                AstUtils.Box(args.TargetExpression)
            );
        }

        protected override Expression/*!*/ MakeErrorExpression(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            return Ast.Throw(MakeNotSymbolNorStringError(args), resultType);
        }

        protected override void SetError(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            metaBuilder.SetError(MakeNotSymbolNorStringError(args));
        }

        internal protected override bool TryImplicitConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            if (base.TryImplicitConversion(metaBuilder, args)) {
                return true;
            }

            object target = args.Target;
            var targetExpression = args.TargetExpression;

            var str = target as MutableString;
            if (str != null) {
                metaBuilder.Result = Methods.ConvertMutableStringToClrString.OpCall(AstUtils.Convert(targetExpression, typeof(MutableString)));
                return true;
            }

            if (target is RubySymbol) {
                metaBuilder.Result = Methods.ConvertSymbolToClrString.OpCall(AstUtils.Convert(targetExpression, typeof(RubySymbol)));
                return true;
            }

            // Ruby 1.8 let an Integer stand for the Symbol with that id; 1.9 removed it, and MRI
            // now reports one as "42 is not a symbol nor a string" like any other wrong type.

            return false;
        }
    }

    #endregion

    #region Array, Hash, Enumerable

    public sealed class ConvertToArrayAction : ConvertToReferenceTypeAction<ConvertToArrayAction, IList> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToAry; } }
        protected override string/*!*/ TargetTypeName { get { return "Array"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToArrayValidator; } }
    }

    public sealed class TryConvertToArrayAction : TryConvertToReferenceTypeAction<TryConvertToArrayAction, IList> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToAry; } }
        protected override string/*!*/ TargetTypeName { get { return "Array"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.TryToArrayValidator; } }
    }

    // TODO: should be like to_s - default to_a is always called w/o call to respond_to?
    public sealed class TryConvertToAAction : TryConvertToReferenceTypeAction<TryConvertToAAction, IList> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToA; } }
        protected override string/*!*/ TargetTypeName { get { return "Array"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.TryToAValidator; } }
    }

    public sealed class ConvertToHashAction : ConvertToReferenceTypeAction<ConvertToHashAction, IDictionary<object, object>> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToHash; } }
        protected override string/*!*/ TargetTypeName { get { return "Hash"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToHashValidator; } }
    }

    public sealed class TryConvertToHashAction : TryConvertToReferenceTypeAction<TryConvertToHashAction, IDictionary<object, object>> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToHash; } }
        protected override string/*!*/ TargetTypeName { get { return "Hash"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.TryToHashValidator; } }
    }

    /// <summary>
    /// Splats the target value. Returns the value if it cannot be splatted.
    /// </summary>
    public abstract class TrySplatAction<TSelf> : ConvertToReferenceTypeAction<TSelf, IList>
        where TSelf : TrySplatAction<TSelf>, new() {

        protected override string/*!*/ TargetTypeName { get { return "Array"; } }

        public override Type/*!*/ ReturnType {
            get { return typeof(object); }
        }

        // return the target object on error:
        protected override Expression/*!*/ MakeErrorExpression(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            return AstUtils.Box(args.TargetExpression);
        }

        // return the target object on error:
        protected override void SetError(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            metaBuilder.Result = AstUtils.Box(args.TargetExpression);
        }

        protected override DynamicMetaObjectBinder/*!*/ GetInteropBinder(RubyContext/*!*/ context, IList<DynamicMetaObject>/*!*/ args, out MethodInfo postProcessor) {
            postProcessor = null;
            return context.MetaBinderFactory.InteropSplat();
        }
    }

    /// <summary>
    /// Splats the target value. Wraps the value in a RubyArray if the value cannot be splatted.
    /// </summary>
    public abstract class SplatAction<TSelf> : ConvertToReferenceTypeAction<TSelf, IList>
        where TSelf : SplatAction<TSelf>, new() {

        protected override string/*!*/ TargetTypeName { get { return "Array"; } }

        /// <summary>
        /// Validator that receives the splattee as well: a conversion answering nil wraps the
        /// splattee in a one-element array (MRI) instead of raising.
        /// </summary>
        protected abstract MethodInfo/*!*/ SplatResultValidator { get; }

        protected override Expression/*!*/ MakeValidatorCall(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Expression/*!*/ result) {
            return SplatResultValidator.OpCall(targetClassNameConstant, AstUtils.Box(args.TargetExpression), AstUtils.Box(result));
        }

        protected internal override bool TryImplicitConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            var convertedTarget = args.Target as IList;
            if (convertedTarget != null) {
                metaBuilder.Result = AstUtils.Convert(args.TargetExpression, typeof(IList));
                return true;
            }

            if (args.Target == null && ConvertsNullToEmptyArray) {
                // `*nil` is no elements at all - MRI 4.0 does not ask nil for #to_a
                metaBuilder.Result = Ast.Convert(Methods.MakeArray0.OpCall(), typeof(IList));
                return true;
            }
            return false;
        }

        /// <summary>
        /// True for the explicit `*x` splat. Destructuring goes through #to_ary instead, where
        /// nil is a value like any other: `(*f) = nil` binds f to [nil].
        /// </summary>
        protected virtual bool ConvertsNullToEmptyArray { get { return false; } }

        // return the target object on error:
        protected override Expression/*!*/ MakeErrorExpression(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            return Ast.Convert(Methods.MakeArray1.OpCall(AstUtils.Box(args.TargetExpression)), typeof(IList));
        }

        // return the target object on error:
        protected override void SetError(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            metaBuilder.Result = Ast.Convert(Methods.MakeArray1.OpCall(AstUtils.Box(args.TargetExpression)), typeof(IList));
        }

        protected override DynamicMetaObjectBinder/*!*/ GetInteropBinder(RubyContext/*!*/ context, IList<DynamicMetaObject>/*!*/ args, out MethodInfo postProcessor) {
            postProcessor = null;
            return context.MetaBinderFactory.InteropSplat();
        }
    }

    /// <summary>
    /// Returns result of "to_a" if the object responds to "to_a" or the target object itself otherwise.
    /// Throws an exception if "to_a" doesn't return IList.
    /// </summary>
    /// <remarks>See also http://redmine.ruby-lang.org/issues/show/3680 </remarks>
    public sealed class ExplicitTrySplatAction : TrySplatAction<ExplicitTrySplatAction> {
        protected sealed override string/*!*/ ToMethodName { get { return Symbols.ToA; } }
        protected sealed override MethodInfo ConversionResultValidator { get { return Methods.ToAValidator; } }
    }

    /// <summary>
    /// Returns result of "to_ary" if the object responds to "to_ary" or the target object itself otherwise.
    /// Throws an exception if "to_ary" doesn't return IList.
    /// </summary>
    /// <remarks>See also http://redmine.ruby-lang.org/issues/show/3680 </remarks>
    public sealed class ImplicitTrySplatAction : TrySplatAction<ImplicitTrySplatAction> {
        protected sealed override string/*!*/ ToMethodName { get { return Symbols.ToAry; } }
        protected sealed override MethodInfo ConversionResultValidator { get { return Methods.ToArrayValidator; } }

        // a #to_ary answering nil leaves the object unsplatted, as MRI's block argument
        // destructuring does, rather than being an error
        protected override Expression/*!*/ MakeValidatorCall(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Expression/*!*/ result) {
            return Methods.TrySplatToAryValidator.OpCall(targetClassNameConstant, AstUtils.Box(args.TargetExpression), AstUtils.Box(result));
        }
    }

    /// <summary>
    /// Returns result of "to_a" if the object responds to "to_a" or the target object itself otherwise.
    /// Throws an exception if "to_a" doesn't return IList.
    /// </summary>
    /// <remarks>See also http://redmine.ruby-lang.org/issues/show/3680 </remarks>
    public sealed class ExplicitSplatAction : SplatAction<ExplicitSplatAction> {
        protected sealed override string/*!*/ ToMethodName { get { return Symbols.ToA; } }
        protected sealed override bool ConvertsNullToEmptyArray { get { return true; } }
        protected sealed override MethodInfo ConversionResultValidator { get { return Methods.ToAValidator; } }
        protected sealed override MethodInfo/*!*/ SplatResultValidator { get { return Methods.SplatToAValidator; } }
    }

    /// <summary>
    /// Returns result of "to_ary" if the object responds to "to_ary" or the target object itself otherwise.
    /// Throws an exception if "to_ary" doesn't return IList.
    /// </summary>
    /// <remarks>See also http://redmine.ruby-lang.org/issues/show/3680 </remarks>
    public sealed class ImplicitSplatAction : SplatAction<ImplicitSplatAction> {
        protected sealed override string/*!*/ ToMethodName { get { return Symbols.ToAry; } }
        protected sealed override MethodInfo ConversionResultValidator { get { return Methods.ToArrayValidator; } }
        protected sealed override MethodInfo/*!*/ SplatResultValidator { get { return Methods.SplatToAryValidator; } }
    }

    #endregion

    #region Integers

    public abstract class ConvertToIntegerAction<TSelf> : ProtocolConversionAction<TSelf>
        where TSelf : ConvertToIntegerAction<TSelf>, new() {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToInt; } }

        internal protected override bool TryImplicitConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            object target = args.Target;

            if (target == null) {
                metaBuilder.SetError(ConversionErrorFactory.OpCall(AstUtils.Constant("nil"), AstUtils.Constant(TargetTypeName)));
                return true;
            }

            return (metaBuilder.Result = Convert(ReturnType, args)) != null;
        }
    }

    public class TryConvertToFixnumAction : ProtocolConversionAction<TryConvertToFixnumAction> {
        protected override string/*!*/ TargetTypeName { get { return "Integer"; } }
        protected override string/*!*/ ToMethodName { get { return Symbols.ToInt; } }
        public override Type/*!*/ ReturnType { get { return typeof(int?); } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToFixnumValidator; } }
        
        internal protected override bool TryImplicitConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            object target = args.Target;
            return target != null && (metaBuilder.Result = Convert(ReturnType, args)) != null;
        }

        // return null if the object doesn't handle the conversion:
        protected override Expression/*!*/ MakeErrorExpression(CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            return AstUtils.Constant(null, resultType);
        }

        // return null if the object doesn't handle the conversion:
        protected override void SetError(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, Expression/*!*/ targetClassNameConstant, Type/*!*/ resultType) {
            metaBuilder.Result = AstUtils.Constant(null, resultType);
        }
    }

    public sealed class ConvertToFixnumAction : ConvertToIntegerAction<ConvertToFixnumAction> {
        protected override string/*!*/ TargetTypeName { get { return "Integer"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToFixnumValidator; } }

    }

    public sealed class ConvertToByteAction : ConvertToIntegerAction<ConvertToByteAction> {
        protected override string/*!*/ TargetTypeName { get { return "System::Byte"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToByteValidator; } }
    }

    public sealed class ConvertToSByteAction : ConvertToIntegerAction<ConvertToSByteAction> {
        protected override string/*!*/ TargetTypeName { get { return "System::SByte"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToSByteValidator; } }
    }

    public sealed class ConvertToInt16Action : ConvertToIntegerAction<ConvertToInt16Action> {
        protected override string/*!*/ TargetTypeName { get { return "System::Int16"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToInt16Validator; } }
    }

    public sealed class ConvertToUInt16Action : ConvertToIntegerAction<ConvertToUInt16Action> {
        protected override string/*!*/ TargetTypeName { get { return "System::UInt16"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToUInt16Validator; } }
    }

    public sealed class ConvertToUInt32Action : ConvertToIntegerAction<ConvertToUInt32Action> {
        protected override string/*!*/ TargetTypeName { get { return "System::UInt32"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToUInt32Validator; } }
    }

    public sealed class ConvertToUInt64Action : ConvertToIntegerAction<ConvertToUInt64Action> {
        protected override string/*!*/ TargetTypeName { get { return "System::UInt64"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToUInt64Validator; } }
    }

    public sealed class ConvertToInt64Action : ConvertToIntegerAction<ConvertToInt64Action> {
        protected override string/*!*/ TargetTypeName { get { return "System::Int64"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToInt64Validator; } }
    }

    public sealed class ConvertToBignumAction : ConvertToIntegerAction<ConvertToBignumAction> {
        protected override string/*!*/ TargetTypeName { get { return "Integer"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToBignumValidator; } }
    }

    #endregion

    #region IntegerValue

    public abstract class ConvertToIntegerValueAction<TSelf> : ProtocolConversionAction<TSelf>
        where TSelf : ConvertToIntegerValueAction<TSelf>, new() {

        protected override string/*!*/ TargetTypeName { get { return "Integer"; } }

        internal protected override bool TryImplicitConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            if (args.Target == null) {
                metaBuilder.SetError(ConversionErrorFactory.OpCall(AstUtils.Constant("nil"), AstUtils.Constant(TargetTypeName)));
                return true;
            }

            metaBuilder.Result =
                ImplicitConvert(typeof(int), args) ??
                ImplicitConvert(typeof(BigInteger), args);

            return metaBuilder.Result != null;
        }
    }

    /// <summary>
    /// Calls to_int and wraps the result (Fixnum or Bignum) into IntegerValue.
    /// </summary>
    public sealed class ConvertToIntAction : ConvertToIntegerValueAction<ConvertToIntAction> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToInt; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToIntegerValidator; } }
        // #to_int is the implicit protocol - what an Integer parameter asks for - and MRI words
        // its failure "no implicit conversion of X into Integer". The explicit wording belongs to
        // #to_i below, which is the step Kernel#Integer(x) and "%d" % x fail at.
    }

    /// <summary>
    /// Calls to_i and wraps the result (Fixnum or Bignum) into IntegerValue.
    /// </summary>
    public sealed class ConvertToIAction : ConvertToIntegerValueAction<ConvertToIAction> {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToI; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToIntegerValidator; } }

        // Kernel#Integer(x) and "%d" % x end here; MRI words those "can't convert nil into
        // Integer", not "no implicit conversion ...".
        protected override MethodInfo/*!*/ ConversionErrorFactory {
            get { return Methods.CreateTypeConversionError; }
        }
    }

    #endregion

    #region Floating Point

    /// <summary>
    /// Calls to_f (in most cases) and wraps the result into double. It directly calls Kernel.Float for String, Fixnum and Bignum.
    /// </summary>
    public abstract class ConvertToFloatingPointAction<TSelf> : ProtocolConversionAction<TSelf>
        where TSelf : ConvertToFloatingPointAction<TSelf>, new() {
        protected override string/*!*/ ToMethodName { get { return Symbols.ToF; } }

        // Kernel#Float(x): MRI says "can't convert nil into Float".
        protected override MethodInfo/*!*/ ConversionErrorFactory {
            get { return Methods.CreateTypeConversionError; }
        }

        internal protected override bool TryImplicitConversion(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            if (args.Target == null) {
                metaBuilder.SetError(ConversionErrorFactory.OpCall(AstUtils.Constant("nil"), AstUtils.Constant(TargetTypeName)));
                return true;
            }

            metaBuilder.Result = Convert(ReturnType, args) ?? FromString(args);

            return metaBuilder.Result != null;
        }

        private static Expression FromString(CallArguments/*!*/ args) {
            var result = ImplicitConvert(typeof(MutableString), args);
            if (result != null) {
                return Ast.Call(Methods.ConvertMutableStringToFloat, args.MetaContext.Expression, result);
            }

            result = ImplicitConvert(typeof(string), args);
            if (result != null) {
                return Ast.Call(Methods.ConvertStringToFloat, args.MetaContext.Expression, result);
            }

            return null;
        }
    }

    public sealed class ConvertToFAction : ConvertToFloatingPointAction<ConvertToFAction> {
        protected override string/*!*/ TargetTypeName { get { return "Float"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToDoubleValidator; } }
    }

    public sealed class ConvertToSingleAction : ConvertToFloatingPointAction<ConvertToSingleAction> {
        protected override string/*!*/ TargetTypeName { get { return "System::Single"; } }
        protected override MethodInfo ConversionResultValidator { get { return Methods.ToSingleValidator; } }
    }

    #endregion
}

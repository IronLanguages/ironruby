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
using System.Dynamic;
using System.Reflection;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Actions.Calls;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Interpreter;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime.Calls {

    /// <summary>
    /// How a call of one overload of a library method is made without a rule: the overload's parameters as the
    /// overload resolver (<see cref="RubyOverloadResolver"/>) would fill them, for the shapes where what it does
    /// can be told from the argument values alone - hidden storages, the scope or context, the block, self,
    /// positional arguments that need no conversion or the parameter's default protocol conversion, defaults
    /// and a params array. Any other overload has no plan, and its calls get rules as before.
    /// </summary>
    internal sealed class LibraryCallPlan {
        private enum Kind : byte {
            Storage,
            Scope,
            Context,
            Block,
            Self,
            Positional,
            Params,
        }

        private sealed class Parameter {
            internal Kind Kind;
            internal Type/*!*/ Type;
            internal bool NotNull;

            // Storage: the instance; Positional: the default value, if HasDefault
            internal object Constant;
            internal bool HasDefault;

            // the conversion a [DefaultProtocol] parameter (or a params array's items) gets:
            internal LibraryArgumentConversion Conversion;
        }

        private readonly CallInstruction/*!*/ _call;
        private readonly Parameter/*!*/[]/*!*/ _parameters;
        private readonly Type/*!*/ _returnType;

        // positional parameters (mandatory ones first), not counting a params array:
        private readonly int _mandatoryCount;
        private readonly int _positionalCount;
        private readonly Parameter _params;

        // the BlockParam parameter and its index, or null (the block is not passed to the overload):
        private readonly Parameter _block;
        private readonly int _blockIndex;

        private LibraryCallPlan(CallInstruction/*!*/ call, Parameter/*!*/[]/*!*/ parameters, Type/*!*/ returnType,
            int mandatoryCount, int positionalCount, Parameter paramsParameter, Parameter block) {
            _call = call;
            _parameters = parameters;
            _returnType = returnType;
            _mandatoryCount = mandatoryCount;
            _positionalCount = positionalCount;
            _params = paramsParameter;
            _block = block;
            _blockIndex = Array.IndexOf(parameters, block);
        }

        internal bool TakesBlock {
            get { return _block != null; }
        }

        internal bool AcceptsCount(int count) {
            return count >= _mandatoryCount && (_params != null || count <= _positionalCount);
        }

        /// <summary>
        /// Whether the resolver would find this overload callable with these arguments; the caller has checked the count.
        /// A block: whether one is passed (<paramref name="hasBlock"/>, the signature's) and which (<paramref name="block"/>, maybe null).
        /// </summary>
        internal bool Accepts(object self, bool hasBlock, Proc block, object[]/*!*/ args) {
            if (_block != null && _block.NotNull && block == null) {
                return false;
            }

            int a = 0;
            foreach (var p in _parameters) {
                switch (p.Kind) {
                    case Kind.Self:
                        if (!AcceptsValue(p, self)) {
                            return false;
                        }
                        break;

                    case Kind.Positional:
                        if (a < args.Length && !AcceptsValue(p, args[a])) {
                            return false;
                        }
                        a++;
                        break;

                    case Kind.Params:
                        for (int i = a; i < args.Length; i++) {
                            if (!AcceptsItem(p, args[i])) {
                                return false;
                            }
                        }
                        break;
                }
            }
            return true;
        }

        // Converter.CanConvertFrom of the value to the parameter, on some narrowing level - with the cases it would answer
        // otherwise, or the resolver would treat differently, left to the rule.
        private static bool AcceptsValue(Parameter/*!*/ p, object value) {
            if (value == null) {
                // DynamicNull: not convertible to [NotNull]; a reference type or Boolean gets null or false; a value type only by
                // its protocol conversion, which the [DefaultProtocol] parameter has:
                return !p.NotNull && (!p.Type.IsValueType || p.Type == typeof(bool) || p.Conversion != null);
            }

            if (p.Conversion != null) {
                // the default protocol conversion converts anything that is not nil (on narrowing level 1, if not on 0):
                return true;
            }

            // identity, a reference conversion or unboxing; anything else is a conversion the rule makes:
            return p.Type.IsValueType ? value.GetType() == p.Type : p.Type.IsInstanceOfType(value);
        }

        private static bool AcceptsItem(Parameter/*!*/ p, object value) {
            if (value == null) {
                return !p.NotNull && (!p.Type.IsValueType || p.Type == typeof(bool) || p.Conversion != null);
            }
            return p.Conversion != null || (p.Type.IsValueType ? value.GetType() == p.Type : p.Type.IsInstanceOfType(value));
        }

        // RubyOverloadResolver.Convert of an accepted value:
        private static object ConvertValue(Parameter/*!*/ p, object value) {
            if (p.Conversion != null) {
                return p.Conversion.Convert(value);
            }
            if (value == null && p.Type == typeof(bool)) {
                return ScriptingRuntimeHelpers.False;
            }
            return value;
        }

        internal object/*!*/ Invoke(RubyContext/*!*/ context, RubyScope/*!*/ scope, object self, Proc block, object[]/*!*/ args) {
            var clrArgs = new object[_parameters.Length];
            BlockParam bfc = null;

            int a = 0;
            for (int i = 0; i < _parameters.Length; i++) {
                var p = _parameters[i];
                switch (p.Kind) {
                    case Kind.Storage:
                        clrArgs[i] = p.Constant;
                        break;

                    case Kind.Scope:
                        clrArgs[i] = scope;
                        break;

                    case Kind.Context:
                        clrArgs[i] = context;
                        break;

                    case Kind.Block:
                        // RuleControlFlowBuilder: the BlockParam is made as the method is entered
                        break;

                    case Kind.Self:
                        clrArgs[i] = self;
                        break;

                    case Kind.Positional:
                        clrArgs[i] = (a < args.Length) ? ConvertValue(p, args[a]) : (p.Conversion != null ? p.Conversion.Convert(p.Constant) : p.Constant);
                        a++;
                        break;

                    case Kind.Params:
                        int count = Math.Max(args.Length - a, 0);
                        Array items = (p.Type == typeof(object)) ? new object[count] : Array.CreateInstance(p.Type, count);
                        for (int j = 0; j < count; j++) {
                            items.SetValue(ConvertValue(p, args[a + j]), j);
                        }
                        clrArgs[i] = items;
                        break;
                }
            }

            if (_block != null && block != null) {
                // RubyMethodGroupBase.RuleControlFlowBuilder:
                bfc = RubyOps.CreateBfcForLibraryMethod(block);
                clrArgs[_blockIndex] = bfc;
                try {
                    return Box(_call.Invoke(clrArgs));
                } catch (MethodUnwinder unwinder) when (RubyOps.IsProcConverterTarget(bfc, unwinder)) {
                    return unwinder.ReturnValue;
                } finally {
                    RubyOps.LeaveProcConverter(bfc);
                }
            }

            return Box(_call.Invoke(clrArgs));
        }

        // AstUtils.Convert to object, which the rule's result goes through, boxes these through the shared boxes:
        private object Box(object result) {
            if (_returnType == typeof(bool)) {
                return ScriptingRuntimeHelpers.BooleanToObject((bool)result);
            }
            if (_returnType == typeof(int)) {
                return ScriptingRuntimeHelpers.Int32ToObject((int)result);
            }
            return result;
        }

        /// <summary>
        /// A plan for calling <paramref name="overload"/> of a library method (SelfIsParameter, no implicit protocol conversions), or null.
        /// </summary>
        internal static LibraryCallPlan TryCreate(RubyContext/*!*/ context, OverloadInfo/*!*/ overloadInfo) {
            var overload = overloadInfo as LibraryOverload;
            if (overload == null || overload.Delegate.Target != null) {
                return null;
            }

            var method = overload.ReflectionInfo as MethodInfo;
            if (method == null || !method.IsStatic || method.ContainsGenericParameters) {
                return null;
            }

            var infos = overload.Parameters;
            if (infos.Count > 9) {
                // CallInstruction.Invoke(object[]) calls through a typed delegate up to here
                return null;
            }

            var parameters = new Parameter[infos.Count];
            int i = 0;

            // RubyOverloadResolver.MapSpecialParameters, SelfIsParameter:
            while (i < infos.Count && infos[i].ParameterType.IsSubclassOf(typeof(RubyCallSiteStorage))) {
                object storage;
                try {
                    storage = Activator.CreateInstance(infos[i].ParameterType, context);
                } catch (Exception) {
                    return null;
                }
                parameters[i] = new Parameter { Kind = Kind.Storage, Type = infos[i].ParameterType, Constant = storage };
                i++;
            }

            if (i < infos.Count) {
                if (infos[i].ParameterType == typeof(RubyScope)) {
                    parameters[i] = new Parameter { Kind = Kind.Scope, Type = typeof(RubyScope) };
                    i++;
                } else if (infos[i].ParameterType == typeof(RubyContext)) {
                    parameters[i] = new Parameter { Kind = Kind.Context, Type = typeof(RubyContext) };
                    i++;
                }
            }

            Parameter block = null;
            if (i < infos.Count && infos[i].ParameterType == typeof(BlockParam)) {
                parameters[i] = block = new Parameter { Kind = Kind.Block, Type = typeof(BlockParam), NotNull = overload.ProhibitsNull(i) };
                i++;
            }

            if (i >= infos.Count) {
                return null;
            }

            // self (a [DefaultProtocol] on it would convert it):
            if (!IsPlainParameterType(infos[i].ParameterType) || infos[i].IsDefined(typeof(DefaultProtocolAttribute), false)) {
                return null;
            }
            parameters[i] = new Parameter { Kind = Kind.Self, Type = infos[i].ParameterType, NotNull = overload.ProhibitsNull(i) };
            i++;

            int mandatoryCount = 0;
            int positionalCount = 0;
            Parameter paramsParameter = null;
            for (; i < infos.Count; i++) {
                var info = infos[i];
                var type = info.ParameterType;

                if (overload.IsParamArray(i)) {
                    if (i != infos.Count - 1 || !type.IsArray || type.GetArrayRank() != 1) {
                        return null;
                    }

                    var itemType = type.GetElementType();
                    if (!IsPlainParameterType(itemType)) {
                        return null;
                    }

                    // the default protocol applies to the items of a params array (RubyOverloadResolver.HasExplicitProtocolConversion)
                    LibraryArgumentConversion itemConversion = null;
                    if (info.IsDefined(typeof(DefaultProtocolAttribute), false) && (itemConversion = LibraryArgumentConversion.TryGet(context, itemType)) == null) {
                        return null;
                    }

                    parameters[i] = paramsParameter = new Parameter {
                        Kind = Kind.Params, Type = itemType, NotNull = overload.ProhibitsNullItems(i), Conversion = itemConversion
                    };
                    break;
                }

                if (overload.IsParamDictionary(i) || !IsPlainParameterType(type)) {
                    return null;
                }

                LibraryArgumentConversion conversion = null;
                if (info.IsDefined(typeof(DefaultProtocolAttribute), false) && (conversion = LibraryArgumentConversion.TryGet(context, type)) == null) {
                    return null;
                }

                var p = new Parameter { Kind = Kind.Positional, Type = type, NotNull = overload.ProhibitsNull(i), Conversion = conversion };

                if (info.IsMandatory()) {
                    // a mandatory parameter after an optional one: let the resolver map it
                    if (mandatoryCount != positionalCount) {
                        return null;
                    }
                    mandatoryCount++;
                } else {
                    // DefaultArgBuilder:
                    object value = info.GetDefaultValue();
                    if (value is Missing) {
                        value = CompilerHelpers.GetMissingValue(type);
                    }
                    if (value is DBNull) {
                        return null;
                    }

                    if (conversion == null) {
                        // RubyOverloadResolver.Convert of the constant (restricted to its own type):
                        if (value == null) {
                            if (type == typeof(bool)) {
                                value = ScriptingRuntimeHelpers.False;
                            } else if (type.IsValueType) {
                                return null;
                            }
                        } else if (!(type.IsValueType ? value.GetType() == type : type.IsInstanceOfType(value))) {
                            return null;
                        }
                    }

                    p.HasDefault = true;
                    p.Constant = value;
                }

                parameters[i] = p;
                positionalCount++;
            }

            var returnType = method.ReturnType;
            if (returnType.IsByRef || returnType.IsPointer) {
                return null;
            }

            CallInstruction call;
            try {
                call = CallInstruction.Create(method, method.GetParameters());
            } catch (Exception) {
                return null;
            }

            return new LibraryCallPlan(call, parameters, returnType, mandatoryCount, positionalCount, paramsParameter, block);
        }

        private static bool IsPlainParameterType(Type/*!*/ type) {
            return !type.IsByRef && !type.IsPointer && !type.ContainsGenericParameters &&
                type != typeof(BlockParam) && type != typeof(MissingBlockParam) && type != typeof(DynamicNull) &&
                !typeof(RubyCallSiteStorage).IsAssignableFrom(type) && type != typeof(RubyScope) && type != typeof(RubyContext) &&
                !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>));
        }

        /// <summary>
        /// Whether the overload takes <paramref name="count"/> positional arguments and the block given, if it insists on one.
        /// </summary>
        internal bool AcceptsShape(int count, Proc block) {
            return count >= _mandatoryCount && (_params != null || count <= _positionalCount) && (_block == null || !_block.NotNull || block != null);
        }
    }

    /// <summary>
    /// The default protocol conversion of an argument passed to a [DefaultProtocol] parameter: the conversion action the rule calls
    /// (RubyOverloadResolver.Convert), here through a call site of its own, one per runtime and parameter type.
    /// </summary>
    internal abstract class LibraryArgumentConversion {
        internal abstract object Convert(object value);

        private static readonly ConditionalWeakTable<RubyConversionAction, LibraryArgumentConversion>/*!*/ _conversions =
            new ConditionalWeakTable<RubyConversionAction, LibraryArgumentConversion>();

        internal static LibraryArgumentConversion TryGet(RubyContext/*!*/ context, Type/*!*/ type) {
            var action = RubyConversionAction.TryGetDefaultConversionAction(context, type);
            if (action == null) {
                return null;
            }

            LibraryArgumentConversion result;
            if (_conversions.TryGetValue(action, out result)) {
                return result;
            }

            result = Create(action, type);
            if (result != null) {
                result = _conversions.GetValue(action, _ => result);
            }
            return result;
        }

        // the instantiations are spelled out so that they exist without dynamic code (and are compiled ahead of time)
        private static LibraryArgumentConversion Create(RubyConversionAction/*!*/ action, Type/*!*/ type) {
            if (type == typeof(string)) return new Typed<string>(action);
            if (type == typeof(MutableString)) return new Typed<MutableString>(action);
            if (type == typeof(int)) return new Typed<int>(action);
            if (type == typeof(System.Collections.IList)) return new Typed<System.Collections.IList>(action);
            if (type == typeof(RubyRegex)) return new Typed<RubyRegex>(action);
            if (type == typeof(double)) return new Typed<double>(action);
            if (type == typeof(IntegerValue)) return new Typed<IntegerValue>(action);
            if (type == typeof(IDictionary<object, object>)) return new Typed<IDictionary<object, object>>(action);
            if (type == typeof(Union<int, MutableString>)) return new Typed<Union<int, MutableString>>(action);
            if (type == typeof(Union<MutableString, int>)) return new Typed<Union<MutableString, int>>(action);
            if (type == typeof(long)) return new Typed<long>(action);
            return null;
        }

        private sealed class Typed<T> : LibraryArgumentConversion {
            private readonly CallSite<Func<CallSite, object, T>>/*!*/ _site;

            internal Typed(RubyConversionAction/*!*/ action) {
                _site = CallSite<Func<CallSite, object, T>>.Create(action);
            }

            internal override object Convert(object value) {
                return _site.Target(_site, value);
            }
        }
    }

    /// <summary>
    /// A precompiled dispatcher for the first calls of a library method from a call site - a site that may well never be called again,
    /// as most of the prelude's are not. It calls the method through a <see cref="LibraryCallPlan"/>, on the receiver test of a rule (the
    /// class version, see <see cref="MetaObjectBuilder.AddTargetTypeTest"/>) and the overload resolver's choice made again for each call.
    /// After <see cref="Budget"/> calls it hands the site back to the binder, which then builds the rule a site that is hot should have.
    /// </summary>
    public abstract partial class LibraryDispatcher : MemberDispatcher {
        /// <summary>
        /// The calls a site makes through the dispatcher before it gets a rule: as many as the interpreter runs a rule for before
        /// compiling it, so that a site that is called this often pays for the rule only where it also pays for its compilation.
        /// </summary>
        internal const int DefaultBudget = 32;

        internal static int Budget = DefaultBudget;

        private RubyContext/*!*/ _context;
        private LibraryCallPlan/*!*/ _plan;
        private bool _hasBlock;

        // A method of several overloads: the resolver picked the plan's for arguments of these types (null for nil) and a block
        // given or not. The choice is a function of them (it made no assumptions upon values), so it holds for a call that passes
        // the same. A method of one overload: null, the plan says whether a call can be made (LibraryCallPlan.Accepts).
        private Type[] _argTypes;
        private bool _blockGiven;

        // the receiver test: IRubyObject - its exact type and its immediate class's version; nil or a CLR object (plain type test,
        // see RubyCallAction.HasPlainClrTypeTest) - its exact type (null for nil) and the version of its class:
        private bool _isRubyObject;
        private Type _targetType;
        private VersionHandle _classVersion;

        private int _calls;

        internal bool IsExhausted {
            get { return _calls >= Budget; }
        }

        internal static bool IsExhaustedDispatcher(object rule) {
            var d = rule as Delegate;
            var dispatcher = (d != null) ? d.Target as LibraryDispatcher : null;
            return dispatcher != null && dispatcher.IsExhausted;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsCurrent(object self) {
            if (_isRubyObject) {
                return self != null && self.GetType() == _targetType && ((IRubyObject)self).ImmediateClass.Version.Method == Version;
            }
            return (self == null ? _targetType == null : self.GetType() == _targetType) && _classVersion.Method == Version;
        }

        internal bool TryInvoke(RubyScope/*!*/ scope, object self, object blockArg, object[]/*!*/ args, out object result) {
            Proc block = blockArg as Proc;
            if (_calls >= Budget || blockArg != null && block == null || !Matches(block, args) || !_plan.Accepts(self, _hasBlock, block, args)) {
                result = null;
                return false;
            }

            _calls++;
            result = _plan.Invoke(_context, scope, self, _hasBlock ? block : null, args);
            return true;
        }

        private bool Matches(Proc block, object[]/*!*/ args) {
            var types = _argTypes;
            if (types == null) {
                return _plan.AcceptsShape(args.Length, block);
            }

            if ((block != null) != _blockGiven || args.Length != types.Length) {
                return false;
            }
            for (int i = 0; i < args.Length; i++) {
                var arg = args[i];
                if ((arg != null ? arg.GetType() : null) != types[i]) {
                    return false;
                }
            }
            return true;
        }

        internal RubyScope/*!*/ GetScope(object scope) {
            return (RubyScope)scope ?? _context.EmptyScope;
        }

        /// <param name="plainClrTypeTest">The receiver is a CLR object whose rule would test its type and class version only (see RubyCallAction.HasPlainClrTypeTest).</param>
        internal static object TryCreate(RubyCallAction/*!*/ binder, Type/*!*/ delegateType, RubyCallSignature signature, RubyLibraryMethodInfo/*!*/ method,
            RubyClass/*!*/ targetClass, object target, bool plainClrTypeTest, int version, object[]/*!*/ args) {

            // The type test of the rule (see MetaObjectBuilder.AddTargetTypeTest) we can do without the rule: a Ruby object's,
            // nil's and the plain one of a CLR object (not true/false, which are told apart by value):
            if (Budget <= 0) {
                return null;
            }

            bool isRubyObject = target is IRubyObject;
            if (!isRubyObject && target != null && (target is bool || !plainClrTypeTest)) {
                return null;
            }

            // a foreign dynamic object is bound by its own meta-object (RubyMetaBinder.InteropBind)
            if (target is IDynamicMetaObjectProvider && !(target is IRubyDynamicMetaObjectProvider) ||
                target != null && TypeUtils.IsComObjectType(target.GetType())) {
                return null;
            }

            int first = (signature.HasScope ? 2 : 1) + (signature.HasBlock ? 1 : 0);
            int count = args.Length - first;
            if (count < 0 || count > MaxLibraryArity) {
                return null;
            }

            Proc block = signature.HasBlock ? args[first - 1] as Proc : null;
            if (signature.HasBlock && args[first - 1] != null && block == null) {
                return null;
            }

            var callArgs = new object[count];
            Array.Copy(args, first, callArgs, 0, count);

            Type[] argTypes = null;
            LibraryCallPlan plan;
            if (method.MethodBases.Length == 1) {
                plan = method.GetCallPlan(0);
                if (plan != null && !plan.AcceptsShape(count, block)) {
                    plan = null;
                }
            } else {
                argTypes = new Type[count];
                for (int i = 0; i < count; i++) {
                    argTypes[i] = (callArgs[i] != null) ? callArgs[i].GetType() : null;
                }
                int index = method.GetResolvedOverload(binder, target, argTypes, signature.HasBlock, block != null, args);
                plan = (index >= 0) ? method.GetCallPlan(index) : null;
            }

            if (plan == null || !plan.Accepts(target, signature.HasBlock, block, callArgs)) {
                return null;
            }

            var dispatcher = CreateDispatcher(count, signature.HasScope, signature.HasBlock);
            dispatcher._context = targetClass.Context;
            dispatcher._plan = plan;
            dispatcher._hasBlock = signature.HasBlock;
            dispatcher._argTypes = argTypes;
            dispatcher._blockGiven = block != null;
            dispatcher._isRubyObject = isRubyObject;
            dispatcher._targetType = (target != null) ? target.GetType() : null;
            dispatcher._classVersion = targetClass.Version;
            dispatcher.Version = version;

            object result = dispatcher.CreateDelegate(MethodDispatcher.UntypedFuncs.Contains(delegateType));
            return (result.GetType() == delegateType) ? result : null;
        }

        /// <summary>
        /// The index of the overload the rule would call (RubyMethodGroupBase.ResolveOverload) - if the choice depends on the types
        /// of the arguments only - or -1.
        /// </summary>
        internal static int Resolve(RubyCallAction/*!*/ binder, RubyLibraryMethodInfo/*!*/ method, object[]/*!*/ args) {
            var metaArgs = new DynamicMetaObject[args.Length - 1];
            for (int i = 0; i < metaArgs.Length; i++) {
                metaArgs[i] = DynamicMetaObject.Create(args[i + 1], Expression.Parameter(typeof(object)));
            }
            var first = DynamicMetaObject.Create(args[0], Expression.Parameter(args[0] is RubyScope ? typeof(RubyScope) : typeof(object)));

            var callArgs = new CallArguments(binder.Context, first, metaArgs, binder.Signature);
            var metaBuilder = new MetaObjectBuilder(binder, metaArgs);
            var overloads = method.MethodBases;

            RubyOverloadResolver resolver;
            var bindingTarget = RubyMethodGroupBase.ResolveOverload(metaBuilder, callArgs, binder.MethodName, overloads,
                method.CallConvention, method.ImplicitProtocolConversions, out resolver);

            if (!bindingTarget.Success || resolver.HasArgumentAssumptions ||
                ReferenceEquals(bindingTarget.Overload.ReflectionInfo, Methods.CreateDefaultInstance)) {
                return -1;
            }

            for (int i = 0; i < overloads.Length; i++) {
                if (ReferenceEquals(overloads[i], bindingTarget.Overload)) {
                    return i;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// The arguments a call of a library method of several overloads was resolved for: the receiver's and the arguments' types
    /// (null for nil), whether the call site passes a block and whether it was given one.
    /// </summary>
    internal sealed class LibraryResolutionKey : IEquatable<LibraryResolutionKey> {
        private readonly Type _targetType;
        private readonly Type[]/*!*/ _argTypes;
        private readonly bool _hasBlock, _blockGiven;
        private readonly int _hash;

        internal LibraryResolutionKey(Type targetType, Type[]/*!*/ argTypes, bool hasBlock, bool blockGiven) {
            _targetType = targetType;
            _argTypes = argTypes;
            _hasBlock = hasBlock;
            _blockGiven = blockGiven;

            int hash = (targetType != null ? targetType.GetHashCode() : 0) ^ (hasBlock ? 1 : 0) ^ (blockGiven ? 2 : 0) ^ (argTypes.Length << 2);
            foreach (var t in argTypes) {
                hash = hash * 31 + (t != null ? t.GetHashCode() : 0);
            }
            _hash = hash;
        }

        public override int GetHashCode() {
            return _hash;
        }

        public override bool Equals(object obj) {
            return Equals(obj as LibraryResolutionKey);
        }

        public bool Equals(LibraryResolutionKey other) {
            if (other == null || other._hash != _hash || other._targetType != _targetType || other._hasBlock != _hasBlock ||
                other._blockGiven != _blockGiven || other._argTypes.Length != _argTypes.Length) {
                return false;
            }
            for (int i = 0; i < _argTypes.Length; i++) {
                if (_argTypes[i] != other._argTypes[i]) {
                    return false;
                }
            }
            return true;
        }
    }
}

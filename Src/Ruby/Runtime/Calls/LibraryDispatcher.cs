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

using System.Linq.Expressions;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
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

        // the call of the overload's method: through its entry point, or a CallInstruction if its shape has no such call
        private readonly LibraryFunctionCall _function;
        private readonly CallInstruction _call;
        private readonly Parameter/*!*/[]/*!*/ _parameters;
        private readonly Type/*!*/ _returnType;

        // positional parameters (mandatory ones first), not counting a params array:
        private readonly int _mandatoryCount;
        private readonly int _positionalCount;
        private readonly Parameter _params;

        // the BlockParam parameter and its index, or null (the block is not passed to the overload):
        private readonly Parameter _block;
        private readonly int _blockIndex;

        private LibraryCallPlan(LibraryFunctionCall function, CallInstruction call, Parameter/*!*/[]/*!*/ parameters, Type/*!*/ returnType,
            int mandatoryCount, int positionalCount, Parameter paramsParameter, Parameter block) {
            Debug.Assert((function != null) != (call != null));
            _function = function;
            _call = call;
            _parameters = parameters;
            _returnType = returnType;
            _mandatoryCount = mandatoryCount;
            _positionalCount = positionalCount;
            _params = paramsParameter;
            _block = block;
            _blockIndex = Array.IndexOf(parameters, block);
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

            // identity, a reference conversion, unboxing or a widening of a number; anything else is a conversion the rule makes:
            return p.Type.IsValueType ? value.GetType() == p.Type || IsNumericWidening(value.GetType(), p.Type) : p.Type.IsInstanceOfType(value);
        }

        private static bool AcceptsItem(Parameter/*!*/ p, object value) {
            if (value == null) {
                return !p.NotNull && (!p.Type.IsValueType || p.Type == typeof(bool) || p.Conversion != null);
            }
            return p.Conversion != null ||
                (p.Type.IsValueType ? value.GetType() == p.Type || IsNumericWidening(value.GetType(), p.Type) : p.Type.IsInstanceOfType(value));
        }

        // The implicit conversions of Ruby's integers on narrowing level None (Converter.CanConvertFrom: an implicit numeric
        // conversion, BigInteger's op_Implicit), which Converter.ImplicitConvert makes the same way: a Fixnum to an Int64, a Float
        // or a Bignum parameter, an Int64 to a Float or a Bignum one.
        private static bool IsNumericWidening(Type/*!*/ from, Type/*!*/ to) {
            return from == typeof(int) && (to == typeof(long) || to == typeof(double) || to == typeof(BigInteger)) ||
                from == typeof(long) && (to == typeof(double) || to == typeof(BigInteger));
        }

        private static object/*!*/ Widen(object/*!*/ value, Type/*!*/ to) {
            if (value is int) {
                int i = (int)value;
                return (to == typeof(long)) ? (object)(long)i : (to == typeof(double)) ? (object)(double)i : (object)new BigInteger(i);
            }
            long l = (long)value;
            return (to == typeof(double)) ? (object)(double)l : (object)new BigInteger(l);
        }

        // RubyOverloadResolver.Convert of an accepted value:
        private static object ConvertValue(Parameter/*!*/ p, object value) {
            if (p.Conversion != null) {
                return p.Conversion.Convert(value);
            }
            if (value == null) {
                return (p.Type == typeof(bool)) ? ScriptingRuntimeHelpers.False : null;
            }
            if (p.Type.IsValueType && value.GetType() != p.Type) {
                return Widen(value, p.Type);
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
                        clrArgs[i] = ConvertValue(p, self);
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
                    return Call(clrArgs);
                } catch (MethodUnwinder unwinder) when (RubyOps.IsProcConverterTarget(bfc, unwinder)) {
                    return unwinder.ReturnValue;
                } finally {
                    RubyOps.LeaveProcConverter(bfc);
                }
            }

            return Call(clrArgs);
        }

        private object Call(object[]/*!*/ clrArgs) {
            return (_function != null) ? _function.Invoke(clrArgs) : Box(_call.Invoke(clrArgs));
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
                    storage = RubyCallSiteStorage.Create(infos[i].ParameterType, context);
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
                    // optional parameters and a params array make candidates the resolver tells apart by other rules
                    if (i != infos.Count - 1 || !type.IsArray || type.GetArrayRank() != 1 || positionalCount != mandatoryCount) {
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

            // the method's parameters, which the overload's are (LibraryOverload.Parameters, but as an array):
            var clrParameters = method.GetParameters();
            var function = LibraryFunctionCall.TryCreate(method, clrParameters);
            CallInstruction call = null;
            if (function == null) {
                try {
                    call = CallInstruction.Create(method, clrParameters);
                } catch (Exception) {
                    return null;
                }
            }

            return new LibraryCallPlan(function, call, parameters, returnType, mandatoryCount, positionalCount, paramsParameter, block);
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

        /// <summary>
        /// Whether the rule of a call of this overload - its only one - tests the type of the argument at <paramref name="index"/> (the
        /// positional arguments, then a params array's items), passed to the site as a <paramref name="siteType"/>: the overload
        /// resolver restricts an argument to its type unless the parameter takes anything of the site's type (OverloadResolver.
        /// GetRestrictedArgs). So the rule bound for one call makes any call that passes arguments of the same types where they are
        /// tested, whatever the others are - even nil to a [NotNull] Object.
        /// </summary>
        internal bool RestrictsArgument(int index, Type/*!*/ siteType) {
            int positional = 0;
            foreach (var p in _parameters) {
                if (p.Kind == Kind.Positional && positional++ == index || p.Kind == Kind.Params) {
                    return !p.Type.IsAssignableFrom(siteType);
                }
            }
            return true;
        }
    }

    /// <summary>
    /// The overload of a library method of several that the resolver picks for a call (RubyOverloadResolver; OverloadResolver
    /// in the DLR), in two cases that need no conversion analysis: the only overload taking the arguments given, and the
    /// overload that the resolver prefers to every other on the parameters' types alone. The resolver goes narrowing level by
    /// level and takes the best of the candidates applicable on the first level that has any; a candidate whose parameter
    /// types the arguments' types are assignable to is applicable on level None, and one that is exactly as good as another on
    /// each parameter or better (SelectBestConversionFor: the parameter of the argument's own type; of two such, the [NotNull]
    /// one) and better on at least one is preferred to it (OverloadResolver.GetPreferredParameters), whatever level that other
    /// one is applicable on. Anything else is left to the resolver.
    /// </summary>
    internal sealed class LibraryOverloadSelector {
        // The parameters a candidate of each overload has for a call (RubyOverloadResolver.MapSpecialParameters): the block
        // (MissingBlockParam if the overload takes none), self and the positional ones, a params array's items expanded.
        private sealed class Shape {
            internal Type/*!*/ BlockType;
            internal bool BlockNotNull;
            internal Type/*!*/ SelfType;
            internal bool SelfNotNull;
            internal Type/*!*/[]/*!*/ Positional;
            internal bool[]/*!*/ PositionalNotNull;
            internal int Mandatory;
            internal Type ItemType;
            internal bool ItemNotNull;

            internal bool Takes(int count, Proc block) {
                return count >= Mandatory && (ItemType != null || count <= Positional.Length) && !(BlockNotNull && block == null);
            }

            internal Type/*!*/ GetType(int position, out bool notNull) {
                switch (position) {
                    case 0: notNull = BlockNotNull; return BlockType;
                    case 1: notNull = SelfNotNull; return SelfType;
                }
                int i = position - 2;
                if (i < Positional.Length) {
                    notNull = PositionalNotNull[i];
                    return Positional[i];
                }
                notNull = ItemNotNull;
                return ItemType;
            }
        }

        private readonly Shape/*!*/[]/*!*/ _shapes;

        private LibraryOverloadSelector(Shape/*!*/[]/*!*/ shapes) {
            _shapes = shapes;
        }

        /// <summary>
        /// Null if some overload's parameters can't be told (by-ref, a params dictionary).
        /// </summary>
        internal static LibraryOverloadSelector TryCreate(OverloadInfo/*!*/[]/*!*/ overloads, SelfCallConvention callConvention) {
            var shapes = new Shape[overloads.Length];
            for (int k = 0; k < overloads.Length; k++) {
                var overload = overloads[k];
                var infos = overload.Parameters;
                int hidden = RubyOverloadResolver.GetHiddenParameterCount(overload, callConvention);
                if (callConvention != SelfCallConvention.SelfIsParameter || hidden == 0) {
                    return null;
                }

                var shape = new Shape { BlockType = typeof(MissingBlockParam) };
                var positional = new List<Type>();
                var positionalNotNull = new List<bool>();
                for (int i = 0; i < infos.Count; i++) {
                    var type = infos[i].ParameterType;
                    if (type.IsByRef || overload.IsParamDictionary(i)) {
                        return null;
                    }
                    if (i < hidden - 1) {
                        if (type == typeof(BlockParam)) {
                            shape.BlockType = type;
                            shape.BlockNotNull = overload.ProhibitsNull(i);
                        }
                    } else if (i == hidden - 1) {
                        shape.SelfType = type;
                        shape.SelfNotNull = overload.ProhibitsNull(i);
                    } else if (overload.IsParamArray(i)) {
                        if (i != infos.Count - 1 || !type.IsArray || shape.Mandatory != positional.Count) {
                            return null;
                        }
                        shape.ItemType = type.GetElementType();
                        shape.ItemNotNull = overload.ProhibitsNullItems(i);
                    } else {
                        if (infos[i].IsMandatory()) {
                            if (shape.Mandatory != positional.Count) {
                                // a mandatory parameter after an optional one
                                return null;
                            }
                            shape.Mandatory++;
                        }
                        positional.Add(type);
                        positionalNotNull.Add(overload.ProhibitsNull(i));
                    }
                }
                shape.Positional = positional.ToArray();
                shape.PositionalNotNull = positionalNotNull.ToArray();
                shapes[k] = shape;
            }
            return new LibraryOverloadSelector(shapes);
        }

        /// <summary>
        /// The index of the overload the resolver picks for the call, -1 if it has to be asked, -2 if no overload takes the arguments.
        /// </summary>
        /// <param name="hasBlock">Whether the call site passes a block (and <paramref name="block"/> is it, maybe null).</param>
        internal int Select(object target, bool hasBlock, Proc block, object[]/*!*/ args) {
            int count = args.Length;
            int first = -1, taking = 0;
            for (int k = 0; k < _shapes.Length; k++) {
                if (_shapes[k].Takes(count, block)) {
                    if (first < 0) {
                        first = k;
                    }
                    taking++;
                }
            }
            if (taking == 0) {
                return -2;
            }
            if (taking == 1) {
                return first;
            }

            // the actual arguments as the resolver has them (RubyOverloadResolver.CreateActualArguments): the block, self, the rest
            var actual = new Type[count + 2];
            actual[0] = !hasBlock ? typeof(MissingBlockParam) : (block != null) ? typeof(BlockParam) : typeof(DynamicNull);
            actual[1] = (target != null) ? target.GetType() : typeof(DynamicNull);
            for (int i = 0; i < count; i++) {
                actual[i + 2] = (args[i] != null) ? args[i].GetType() : typeof(DynamicNull);
            }

            int result = -1;
            for (int c = 0; c < _shapes.Length; c++) {
                var candidate = _shapes[c];
                if (!candidate.Takes(count, block) || !IsApplicableOnLevelNone(candidate, actual)) {
                    continue;
                }

                bool best = true;
                for (int d = 0; d < _shapes.Length && best; d++) {
                    if (d != c && _shapes[d].Takes(count, block) && !IsPreferred(candidate, _shapes[d], actual)) {
                        best = false;
                    }
                }

                if (best) {
                    if (result >= 0) {
                        return -1;
                    }
                    result = c;
                }
            }
            return result;
        }

        // Converter.CanConvertFrom on NarrowingLevel.None of the cases told apart by type alone:
        private static bool IsApplicableOnLevelNone(Shape/*!*/ shape, Type/*!*/[]/*!*/ actual) {
            for (int i = 0; i < actual.Length; i++) {
                bool notNull;
                Type type = shape.GetType(i, out notNull);
                Type arg = actual[i];

                if (i == 0) {
                    // MissingBlockParam converts to BlockParam (not to [NotNull]), BlockParam to either, nil to BlockParam or MissingBlockParam (a class):
                    if (arg == typeof(MissingBlockParam) ? (type == typeof(BlockParam) && notNull) :
                        arg == typeof(DynamicNull) ? notNull : false) {
                        return false;
                    }
                    continue;
                }

                if (arg == typeof(DynamicNull)) {
                    if (notNull || type.IsValueType && type != typeof(bool)) {
                        return false;
                    }
                } else if (!type.IsAssignableFrom(arg)) {
                    return false;
                }
            }
            return true;
        }

        // OverloadResolver.GetPreferredParameters(one, two) == Candidate.One, by the parameter preferences that do not depend on conversions:
        private static bool IsPreferred(Shape/*!*/ one, Shape/*!*/ two, Type/*!*/[]/*!*/ actual) {
            bool better = false;
            for (int i = 0; i < actual.Length; i++) {
                bool notNullOne, notNullTwo;
                Type typeOne = one.GetType(i, out notNullOne);
                Type typeTwo = two.GetType(i, out notNullTwo);
                Type arg = actual[i];

                // ParametersEquivalent:
                if (typeOne == typeTwo && notNullOne == notNullTwo) {
                    continue;
                }

                // RubyOverloadResolver.SelectBestConversionFor:
                if (arg == typeof(DynamicNull)) {
                    // a nil block: BlockParam rather than a missing one
                    if (i == 0 && typeOne == typeof(BlockParam) && typeTwo == typeof(MissingBlockParam)) {
                        better = true;
                        continue;
                    }
                    return false;
                }

                if (typeOne != arg) {
                    return false;
                }

                if (typeTwo != arg || !arg.IsValueType && notNullOne) {
                    better = true;
                    continue;
                }
                return false;
            }
            return better;
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
    /// Once it has made as many calls as the interpreter runs a rule for before it compiles it (see <see cref="IsHot"/>), it hands its
    /// sites back to the binder for good, which then builds the rule a call that is hot should have.
    /// The dispatcher stands in for the interpreted rule, and does what that rule does beyond the call: it saves the interpreted frames to
    /// an exception that passes it (see <see cref="SaveInterpretedFrames"/>).
    /// </summary>
    public abstract partial class LibraryDispatcher : MemberDispatcher {
        /// <summary>
        /// The calls a dispatcher makes before it is spent, if not -1: for tests.
        /// </summary>
        internal static int BudgetOverride = -1;

        // Microsoft.Scripting.Interpreter.LightCompiler.DefaultCompilationThreshold, which a negative threshold stands for:
        private const int DefaultCompilationThreshold = 32;

        // The most spent dispatchers a binder keeps (see IsHot):
        private const int MaxSpentDispatchers = 8;

        /// <summary>
        /// The calls the dispatcher can still make: as many as the interpreter runs a rule for before it compiles it
        /// (RubyContext.CompilationThreshold when the dispatcher is made: 200 while the core prelude loads, 32 after it) - the calls the
        /// rule would have made interpreted, which is all a dispatcher stands in for. So a call made this often pays for a rule only
        /// where it also pays for compiling it, and none where the rule would have been compiled right away (-X:NoAdaptiveCompilation,
        /// a threshold of 0 or 1).
        /// </summary>
        /// <remarks>
        /// The budget is the dispatcher's, as an interpreted rule's count of calls is the rule's: the binder caches the dispatcher, and
        /// every site that finds it there spends the budget too. A spent dispatcher is kept by its binder (<see cref="RubyCallAction.
        /// SpentDispatchers"/>): the next bind of a call it would have taken, from any site, builds the rule (<see cref="IsHot"/>), which
        /// the binder caches in turn for the other sites. A dispatcher of its own for each such site instead, with a budget of its own,
        /// would be spent by the sites that find it in the cache and be followed by another, as long as the sites are called - as would
        /// the dispatchers of a site that sees receivers of several classes, whose spent dispatcher drops out of the site's cache.
        /// No state is kept per site (a weak table of sites is a handle more for every collection to scan).
        /// </remarks>
        private int _remaining;

        // the binder that made the dispatcher, which keeps it once it is spent:
        private RubyCallAction/*!*/ _binder;

        /// <summary>
        /// Whether the binder may make a dispatcher for a call on the target: the context's rules are interpreted for some calls before
        /// they are compiled, and the call is not hot.
        /// </summary>
        internal static bool MayDispatch(RubyCallAction/*!*/ binder, object target) {
            return GetInitialBudget(binder.Context) > 0 && !IsHot(binder, target);
        }

        /// <summary>
        /// Whether a dispatcher the binder made has spent its budget on calls on receivers like the target: one of its spent
        /// dispatchers would take a call on it (its receiver test holds - the target is of the same class, which has not changed since).
        /// </summary>
        internal static bool IsHot(RubyCallAction/*!*/ binder, object target) {
            var spent = Volatile.Read(ref binder.SpentDispatchers);
            if (spent != null) {
                for (int i = 0; i < spent.Length; i++) {
                    if (spent[i].IsCurrent(target)) {
                        return true;
                    }
                }
            }
            return false;
        }

        // A spent dispatcher is kept by its binder (see IsHot) - with the latest others whose class has not changed since (a dispatcher
        // of a class that changed takes no calls ever again, see IsCurrent).
        private void OnSpent() {
            while (true) {
                var spent = Volatile.Read(ref _binder.SpentDispatchers);
                var kept = new List<LibraryDispatcher>(MaxSpentDispatchers) { this };
                if (spent != null) {
                    for (int i = 0; i < spent.Length && kept.Count < MaxSpentDispatchers; i++) {
                        if (spent[i] != this && spent[i]._classVersion.Method == spent[i].Version) {
                            kept.Add(spent[i]);
                        }
                    }
                }
                if (Interlocked.CompareExchange(ref _binder.SpentDispatchers, kept.ToArray(), spent) == spent) {
                    return;
                }
            }
        }

        // One call more made through the dispatcher, of the budget it may make (see _remaining)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Spend() {
            if (--_remaining == 0) {
                OnSpent();
            }
        }

        private void SetBudget(RubyCallAction/*!*/ binder, RubyContext/*!*/ context) {
            _binder = binder;
            _remaining = GetInitialBudget(context);
        }

        private static int GetInitialBudget(RubyContext/*!*/ context) {
            return (BudgetOverride >= 0) ? BudgetOverride : GetInterpretedRuleCalls(context);
        }

        /// <summary>
        /// The compilation threshold of a rule the binder builds for a call on the target: once a dispatcher has spent its budget on
        /// calls like it, they have made the calls the rule would have run interpreted, and the rule is compiled after the fewest the
        /// interpreter allows (in the background, as any rule) - as it would have been by now. The rule's frames then are what they
        /// would have been too: an interpreted rule has an interpreter frame of its own, which shows in the CLR stack a backtrace is
        /// made of.
        /// </summary>
        internal static int GetRuleCompilationThreshold(RubyCallAction/*!*/ binder, object target, RubyContext/*!*/ context) {
            return IsHot(binder, target) ? MinAsyncCompilationThreshold : context.CompilationThreshold;
        }

        // the lowest threshold the interpreter compiles a lambda with in the background rather than before it runs it the first time:
        private const int MinAsyncCompilationThreshold = 2;

        private static int GetInterpretedRuleCalls(RubyContext/*!*/ context) {
            if (context.Options.NoAdaptiveCompilation) {
                return 0;
            }
            int threshold = context.CompilationThreshold;
            if (threshold < 0) {
                return DefaultCompilationThreshold;
            }
            // the interpreter compiles a lambda of threshold 0 or 1 before it runs it (Interpreter.CompileSynchronously):
            return (threshold <= 1) ? 0 : threshold;
        }

        /// <summary>
        /// What the interpreter does for an exception that leaves an interpreted rule (Interpreter.HandleException): it saves the
        /// interpreted frames - the rule's and its callers' - to the exception, unless it has some already. A backtrace is made of
        /// them (RubyStackTraceBuilder) along with the CLR frames the exception passes, which are not the interpreter's when the
        /// methods in between are compiled; without them, the frames of the interpreted callers above would go missing. The rule's
        /// own frame is no Ruby frame, and this dispatcher has no Interpreter.Run frame to stand for it.
        ///
        /// It runs as an exception filter that never catches, not as a catch that rethrows: a rethrow is a second CLR exception
        /// dispatch - stack walk and all - for every raise that leaves a library method, which made it half the cost of a
        /// `raise' rescued in a loop. The filter sees the same state the catch did: it runs in the first pass, before the
        /// finally blocks between the throw and here, but any frame in between that could change the current interpreted
        /// frame (an interpreted or compiled Ruby frame) catches the exception itself, so the first pass never gets here past
        /// one of them.
        /// </summary>
        private static bool SaveInterpretedFrames(Exception/*!*/ exception) {
            var frame = InterpretedFrame.CurrentFrame.Value;
            if (frame != null && InterpretedFrame.GetExceptionStackTrace(exception) == null) {
                exception.SetData(typeof(InterpretedFrameInfo), new List<InterpretedFrameInfo>(frame.GetStackTraceDebugInfo()).ToArray());
            }
            return false;
        }

        private RubyContext/*!*/ _context;
        private LibraryCallPlan/*!*/ _plan;
        private bool _hasBlock;

        // The calls the dispatcher makes are those the rule bound for its first call would make (and no others): the arguments the
        // rule tests the type of are of the types the first call's were (null for nil), and a block is given or not as it was. A
        // method of one overload: the rule tests the arguments it has to convert (see LibraryCallPlan.RestrictsArgument); the
        // others, null in _restricted, are passed as they are. A method of several overloads: every argument is tested - the
        // resolver picked the plan's overload for arguments of these types, and the choice is a function of them (it made no
        // assumptions upon values), so it holds for a call that passes the same. Either way the plan accepts the call (it did the
        // first call, LibraryCallPlan.Accepts).
        private Type/*!*/[]/*!*/ _argTypes;
        private bool[] _restricted;
        private bool _blockGiven;

        // the receiver test: IRubyObject - its exact type and its immediate class's version; nil or a CLR object - its exact type
        // (null for nil) and the version of its class (and the singleton test the rule makes, see RubyCallAction.ClrReceiverTest);
        // true or false - the value too:
        private bool _isRubyObject;
        private Type _targetType;
        private bool _bool;
        private VersionHandle _classVersion;
        private RubyCallAction.ClrReceiverTest _clrTest;

        /// <summary>
        /// Whether the dispatcher may make the call on <paramref name="self"/>: it is not spent and its receiver test holds - before the
        /// site's arguments are packed for <see cref="TryInvoke"/>. A spent dispatcher stays in the binder's cache, where every site that
        /// misses its own rules tries it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool CanCall(object self) {
            return _remaining > 0 && IsCurrent(self);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsCurrent(object self) {
            if (_isRubyObject) {
                return self != null && self.GetType() == _targetType && ((IRubyObject)self).ImmediateClass.Version.Method == Version;
            }
            if (self == null) {
                return _targetType == null && _classVersion.Method == Version;
            }
            if (self.GetType() != _targetType) {
                return false;
            }
            switch (_clrTest) {
                case RubyCallAction.ClrReceiverTest.NonSingleton:
                    return RubyOps.IsClrNonSingletonRuleValid(_context, self, _classVersion, Version);
                case RubyCallAction.ClrReceiverTest.Singleton:
                    return RubyOps.IsClrSingletonRuleValid(_context, self, Version);
                default:
                    return (_targetType != typeof(bool) || (bool)self == _bool) && _classVersion.Method == Version;
            }
        }

        // Func<CallSite, (RubyScope)?, object, (Proc)?, object x N, object>, the shape the generated dispatchers take:
        private static bool IsSiteDelegate(Type/*!*/ delegateType, RubyCallSignature signature) {
            if (!delegateType.IsGenericType) {
                return false;
            }
            var args = GetDelegateTypeArguments(delegateType);
            int i = 1;
            if (signature.HasScope && args[i++] != typeof(RubyScope)) {
                return false;
            }
            if (args[i++] != typeof(object)) {
                return false;
            }
            if (signature.HasBlock && args[i++] != typeof(Proc)) {
                return false;
            }
            for (; i < args.Length; i++) {
                if (args[i] != typeof(object)) {
                    return false;
                }
            }
            return true;
        }

        // Func<CallSite, TSelf, T0, ..., object> with reference types, no scope nor block:
        private static LibraryDispatcher TryCreateTypedDispatcher(Type/*!*/ delegateType, int count, RubyCallSignature signature) {
            if (signature.HasScope || signature.HasBlock || count > 2 || !delegateType.IsGenericType) {
                return null;
            }
            var args = GetDelegateTypeArguments(delegateType);
            if (args.Length != count + 3 || args[0] != typeof(CallSite) || args[args.Length - 1] != typeof(object) ||
                delegateType != (count == 0 ? typeof(Func<,,>) : count == 1 ? typeof(Func<,,,>) : typeof(Func<,,,,>)).MakeGenericType(args)) {
                return null;
            }
            var types = new Type[count + 1];
            for (int i = 0; i < types.Length; i++) {
                types[i] = args[i + 1];
                if (types[i].IsValueType || types[i].IsPointer || types[i].IsByRef) {
                    return null;
                }
            }
            var generic = (count == 0) ? typeof(TypedLibraryDispatcher<>) : (count == 1) ? typeof(TypedLibraryDispatcher<,>) : typeof(TypedLibraryDispatcher<,,>);
            return (LibraryDispatcher)Activator.CreateInstance(generic.MakeGenericType(types));
        }

        internal bool TryInvoke(RubyScope/*!*/ scope, object self, object blockArg, object[]/*!*/ args, out object result) {
            // Method#call: the method is called on the Method's receiver
            if (_methodObject != null && !_methodObject.TryGetReceiver(self, out self)) {
                result = null;
                return false;
            }

            if (_rubyCall != null) {
                // the rule passes the block and the arguments as they are (RubyMethodInfo.BuildCallNoFlow):
                Proc rubyBlock = blockArg as Proc;
                if (_remaining <= 0 || blockArg != null && rubyBlock == null || _splatCount >= 0 && (args = ExpandSplat(args)) == null) {
                    result = null;
                    return false;
                }
                Spend();
                try {
                    result = _rubyCall.Invoke(self, rubyBlock, args);
                } catch (Exception e) when (SaveInterpretedFrames(e)) {
                    throw;
                }
                return true;
            }

            if (_send != null) {
                Proc sendBlock = blockArg as Proc;
                if (_remaining <= 0 || blockArg != null && sendBlock == null || !_send.Matches(args)) {
                    result = null;
                    return false;
                }
                Spend();
                try {
                    result = _send.Invoke(scope, self, sendBlock, args);
                } catch (Exception e) when (SaveInterpretedFrames(e)) {
                    throw;
                }
                return true;
            }

            if (_construction != null) {
                Proc initializerBlock = blockArg as Proc;
                if (_remaining <= 0 || !_construction.IsCurrent(args.Length) || blockArg != null && initializerBlock == null) {
                    result = null;
                    return false;
                }
                Spend();
                try {
                    result = _construction.Invoke(args, initializerBlock);
                } catch (Exception e) when (SaveInterpretedFrames(e)) {
                    throw;
                }
                return true;
            }

            Proc block = blockArg as Proc;
            if (_remaining <= 0 || blockArg != null && block == null ||
                _splatCount >= 0 && ((args = ExpandSplat(args)) == null || _splatCount > 0 && RubyOps.IsKeywordArgumentsHash(args[args.Length - 1])) ||
                !Matches(block, args)) {
                result = null;
                return false;
            }

            Spend();
            try {
                result = _plan.Invoke(_context, scope, self, _hasBlock ? block : null, args);
            } catch (Exception e) when (SaveInterpretedFrames(e)) {
                throw;
            }
            return true;
        }

        // Class#new of the class that is the receiver, instead of a library method call:
        private ObjectConstruction _construction;

        // a call of a Ruby method, instead of a library method call:
        private RubyMemberCall _rubyCall;

        // the call Kernel#send (or __send__) makes, instead of the call of send:
        private SendCall _send;

        // Method#call (#[], #===) of a Method object: the call is of its method, on its receiver (see MethodObjectCall)
        private MethodObjectCall _methodObject;

        // the receiver test is the Method object's: the call of #call resolved for its class
        private void SetMethodObject(MethodObjectCall/*!*/ methodObject) {
            _methodObject = methodObject;
            _isRubyObject = methodObject.IsRubyObject;
            _targetType = methodObject.MethodObjectType;
            _bool = false;
            _classVersion = methodObject.MethodClass.Version;
            _clrTest = methodObject.ClrTest;
            Version = methodObject.Version;
        }

        /// <summary>
        /// Method#call (and #[], #===, the same rule generator) of a Method object made for a method of the kinds whose call does not
        /// depend on the Method object (RubyMethod.BuildInvoke): the rule tests the Method's method, name and receiver's type and calls
        /// the method on the receiver as a call of it would. The dispatcher makes that call as a dispatcher of a call of the method
        /// would (a Ruby method's, a define_method one's, a library method's), with the receiver as self.
        /// </summary>
        internal static object TryCreateMethodObjectCall(RubyCallAction/*!*/ binder, Type/*!*/ delegateType,
            RubyCallSignature signature, RubyMethod/*!*/ method, RubyClass/*!*/ methodClass, RubyCallAction.ClrReceiverTest clrTest, int version,
            object[]/*!*/ args) {

            var info = method.Info;
            Type infoType = info.GetType();
            if (method.IsMethodMissing || RubyCallAction.IsForeignObject(method.Target) ||
                !(method is IRubyObject) && clrTest != RubyCallAction.ClrReceiverTest.Plain) {
                return null;
            }

            var methodObject = new MethodObjectCall(method, methodClass, clrTest, version);
            object receiver = method.Target;
            RubyClass receiverClass = methodClass.Context.GetImmediateClassOf(receiver);

            // the site's arguments with the receiver in the Method's place, which the resolver of a library method's overloads is given:
            var receiverArgs = ArrayUtils.Copy(args);
            receiverArgs[signature.HasScope ? 1 : 0] = receiver;

            if (infoType == typeof(RubyLibraryMethodInfo)) {
                var libraryMethod = (RubyLibraryMethodInfo)info;
                if (TracePoint.CCallTracing || DispatchIntrinsics.IsSend(libraryMethod)) {
                    return null;
                }
                return TryCreate(binder, delegateType, signature, libraryMethod, receiverClass, receiver, RubyCallAction.ClrReceiverTest.Plain,
                    version, receiverArgs, methodObject);
            } else if (infoType != typeof(RubyLambdaMethodInfo) && infoType != typeof(RubyMethodInfo)) {
                return null;
            }

            // a Ruby method is called by the Method's name, an alias maybe
            return TryCreateRubyCall(binder, delegateType, signature, info, receiverClass, receiver, RubyCallAction.ClrReceiverTest.Plain, version,
                receiverArgs, methodObject, method.Name);
        }

        /// <summary>
        /// Kernel#send or BasicObject#__send__ of a symbol: the rule is the call of the method named that the send makes, with
        /// implicit self (DispatchIntrinsics.TryBuildSend) on a test of the symbol; the dispatcher makes that call through a site of
        /// its own (see <see cref="SendCall"/>), which binds it as any call is bound.
        /// </summary>
        internal static object TryCreateSend(RubyCallAction/*!*/ binder, Type/*!*/ delegateType, RubyCallSignature signature, RubyClass/*!*/ targetClass,
            object target, RubyCallAction.ClrReceiverTest clrTest, int version, object[]/*!*/ args) {

            bool isRubyObject = target is IRubyObject;
            if (!isRubyObject && target != null && !(target is bool) && clrTest == RubyCallAction.ClrReceiverTest.None ||
                RubyCallAction.IsForeignObject(target) || signature.HasSplattedArgument || signature.HasRhsArgument) {
                return null;
            }

            int first = (signature.HasScope ? 2 : 1) + (signature.HasBlock ? 1 : 0);
            int count = args.Length - first;
            if (count < 1 || count > MaxLibraryArity || signature.HasBlock && args[first - 1] != null && !(args[first - 1] is Proc)) {
                return null;
            }

            var send = SendCall.TryCreate(targetClass.Context, signature, args[first] as RubySymbol, count - 1);
            if (send == null) {
                return null;
            }

            bool isUntyped = MethodDispatcher.UntypedFuncs.Contains(delegateType);
            if (!isUntyped && !IsSiteDelegate(delegateType, signature)) {
                return null;
            }

            var dispatcher = CreateDispatcher(count, signature.HasScope, signature.HasBlock);
            dispatcher.SetBudget(binder, targetClass.Context);
            dispatcher._context = targetClass.Context;
            dispatcher._send = send;
            dispatcher._isRubyObject = isRubyObject;
            dispatcher._targetType = (target != null) ? target.GetType() : null;
            dispatcher._bool = (target is bool) ? (bool)target : false;
            dispatcher._classVersion = targetClass.Version;
            dispatcher._clrTest = (isRubyObject || target == null || target is bool) ? RubyCallAction.ClrReceiverTest.Plain : clrTest;
            dispatcher.Version = version;

            object result = dispatcher.CreateDelegate(isUntyped);
            return (result.GetType() == delegateType) ? result : null;
        }

        // The length of the splatted list a call passes (see TryCreate, TryCreateRubyCall), -1 if the site splats none.
        private int _splatCount = -1;

        // The arguments of a splatted call, the list's items in place of the list, as its rule makes them (MetaObjectBuilder.
        // AddSplattedArgumentTest, ArgsBuilder); null if the list is not of the length the rule was bound for.
        private object[] ExpandSplat(object[]/*!*/ args) {
            var list = args[args.Length - 1] as IList;
            if (list == null) {
                return null;
            }
            list = RubyOps.SplatRuby2Keywords(list);
            if (list.Count != _splatCount) {
                return null;
            }
            int simpleCount = args.Length - 1;
            var result = new object[simpleCount + list.Count];
            Array.Copy(args, result, simpleCount);
            for (int i = 0; i < list.Count; i++) {
                result[simpleCount + i] = list[i];
            }
            return result;
        }

        /// <summary>
        /// A call of a Ruby method that the precompiled dispatchers of Ruby methods do not make (see <see cref="RubyMethodCallPlan"/>).
        /// </summary>
        /// <param name="clrTest">The test of a CLR object receiver (not nil, true or false) the rule would make.</param>
        /// <param name="methodObject">A call of Method#call that calls the method, on <paramref name="target"/> (see <see cref="MethodObjectCall"/>).</param>
        /// <param name="name">The name a Ruby method (RubyMethodInfo) is called by, if not its own: an alias.</param>
        internal static object TryCreateRubyCall(RubyCallAction/*!*/ binder, Type/*!*/ delegateType, RubyCallSignature signature, RubyMemberInfo/*!*/ method,
            RubyClass/*!*/ targetClass, object target, RubyCallAction.ClrReceiverTest clrTest, int version, object[]/*!*/ args,
            MethodObjectCall methodObject = null, string name = null) {

            bool isRubyObject = target is IRubyObject;
            if (!isRubyObject && target != null && !(target is bool) && clrTest == RubyCallAction.ClrReceiverTest.None ||
                RubyCallAction.IsForeignObject(target)) {
                return null;
            }

            int first = (signature.HasScope ? 2 : 1) + (signature.HasBlock ? 1 : 0);
            int count = args.Length - first;
            if (count < 0 || count > MaxLibraryArity || signature.HasBlock && args[first - 1] != null && !(args[first - 1] is Proc)) {
                return null;
            }

            // A splatted call passes the items of the list the site's last argument is (ArgsBuilder.AddCallArguments) - after a
            // trailing ruby2_keywords hash in it has been made keyword arguments again - and its rule holds for lists of that length:
            int argumentCount = count;
            int splatCount = -1;
            if (signature.HasSplattedArgument) {
                var list = (count > 0) ? args[args.Length - 1] as IList : null;
                if (list == null || signature.HasRhsArgument) {
                    return null;
                }
                splatCount = RubyOps.SplatRuby2Keywords(list).Count;
                argumentCount = count - 1 + splatCount;
            }

            RubyMemberCall plan;
            if (method is RubyMethodInfo) {
                plan = RubyMethodCallPlan.TryCreate((RubyMethodInfo)method, argumentCount, name ?? ((RubyMethodInfo)method).DefinitionName);
            } else if (method is RubyLambdaMethodInfo && splatCount < 0) {
                plan = LambdaMethodCall.TryCreate((RubyLambdaMethodInfo)method, argumentCount);
            } else {
                plan = null;
            }
            if (plan == null) {
                return null;
            }

            bool isUntyped = MethodDispatcher.UntypedFuncs.Contains(delegateType);
            LibraryDispatcher dispatcher;
            if (isUntyped || IsSiteDelegate(delegateType, signature)) {
                dispatcher = CreateDispatcher(count, signature.HasScope, signature.HasBlock);
            } else if ((dispatcher = TryCreateTypedDispatcher(delegateType, count, signature)) == null) {
                return null;
            }

            dispatcher.SetBudget(binder, targetClass.Context);
            dispatcher._context = targetClass.Context;
            dispatcher._rubyCall = plan;
            dispatcher._splatCount = splatCount;
            dispatcher._isRubyObject = isRubyObject;
            dispatcher._targetType = (target != null) ? target.GetType() : null;
            dispatcher._bool = (target is bool) ? (bool)target : false;
            dispatcher._classVersion = targetClass.Version;
            dispatcher._clrTest = (isRubyObject || target == null || target is bool) ? RubyCallAction.ClrReceiverTest.Plain : clrTest;
            dispatcher.Version = version;

            if (methodObject != null) {
                dispatcher.SetMethodObject(methodObject);
            }

            object result = dispatcher.CreateDelegate(isUntyped);
            return (result.GetType() == delegateType) ? result : null;
        }

        /// <summary>
        /// Class#new of a Ruby class whose instances are plain Ruby objects, made without a rule (see RubyClass.TryGetObjectConstruction).
        /// </summary>
        internal static object TryCreateConstruction(RubyCallAction/*!*/ binder, Type/*!*/ delegateType, RubyCallSignature signature, RubyClass/*!*/ cls,
            RubyClass/*!*/ targetClass, int version, object[]/*!*/ args) {

            int first = (signature.HasScope ? 2 : 1) + (signature.HasBlock ? 1 : 0);
            int count = args.Length - first;
            if (count < 0 || count > MaxLibraryArity || !MethodDispatcher.UntypedFuncs.Contains(delegateType) &&
                !IsSiteDelegate(delegateType, signature)) {
                return null;
            }

            var construction = cls.TryGetObjectConstruction(count);
            if (construction == null) {
                return null;
            }

            // A block goes to a Ruby initializer (which the rule calls with the RFC frame a break from the block unwinds to, see
            // ObjectConstruction.Invoke); the rule of a construction that calls none ignores it in ways of its own.
            if (signature.HasBlock && (!construction.CallsInitializer || args[first - 1] != null && !(args[first - 1] is Proc))) {
                return null;
            }

            var dispatcher = CreateDispatcher(count, signature.HasScope, signature.HasBlock);
            dispatcher.SetBudget(binder, cls.Context);
            dispatcher._context = cls.Context;
            dispatcher._construction = construction;
            dispatcher._isRubyObject = true;
            dispatcher._targetType = cls.GetType();
            dispatcher._classVersion = targetClass.Version;
            dispatcher.Version = version;

            object result = dispatcher.CreateDelegate(MethodDispatcher.UntypedFuncs.Contains(delegateType));
            return (result.GetType() == delegateType) ? result : null;
        }

        private bool Matches(Proc block, object[]/*!*/ args) {
            var types = _argTypes;
            if ((block != null) != _blockGiven || args.Length != types.Length) {
                return false;
            }
            var restricted = _restricted;
            for (int i = 0; i < args.Length; i++) {
                var arg = args[i];
                if ((restricted == null || restricted[i]) && (arg != null ? arg.GetType() : null) != types[i]) {
                    return false;
                }
            }
            return true;
        }

        internal RubyScope/*!*/ GetScope(object scope) {
            return (RubyScope)scope ?? _context.EmptyScope;
        }

        /// <param name="clrTest">The test of a CLR object receiver (not nil, true or false) the rule would make.</param>
        /// <param name="methodObject">A call of Method#call that calls the method, on <paramref name="target"/> (see <see cref="MethodObjectCall"/>).</param>
        internal static object TryCreate(RubyCallAction/*!*/ binder, Type/*!*/ delegateType, RubyCallSignature signature,
            RubyLibraryMethodInfo/*!*/ method, RubyClass/*!*/ targetClass, object target, RubyCallAction.ClrReceiverTest clrTest, int version,
            object[]/*!*/ args, MethodObjectCall methodObject = null) {

            // The type test of the rule (see MetaObjectBuilder.AddTargetTypeTest) we can do without the rule: a Ruby object's,
            // nil's and the plain one of a CLR object, true's and false's (told apart by value):
            bool isRubyObject = target is IRubyObject;
            if (!isRubyObject && target != null && !(target is bool) && clrTest == RubyCallAction.ClrReceiverTest.None) {
                return null;
            }

            // a foreign dynamic object is bound by its own meta-object (RubyMetaBinder.InteropBind)
            if (RubyCallAction.IsForeignObject(target)) {
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

            // A splatted call passes the items of the list in its last argument, a trailing ruby2_keywords hash made keyword
            // arguments again (see ExpandSplat); the resolver takes them as it takes arguments (RubyOverloadResolver.
            // CreateActualArguments), and the rule holds for lists of that length. Keyword arguments are left to the rule.
            int simpleCount = count;
            int splatCount = -1;
            if (signature.HasSplattedArgument) {
                var list = (count > 0) ? callArgs[count - 1] as IList : null;
                if (list == null || signature.HasRhsArgument) {
                    return null;
                }
                list = RubyOps.SplatRuby2Keywords(list);
                simpleCount = count - 1;
                splatCount = list.Count;
                var expanded = new object[simpleCount + splatCount];
                Array.Copy(callArgs, expanded, simpleCount);
                list.CopyTo(expanded, simpleCount);
                if (splatCount > 0 && RubyOps.IsKeywordArgumentsHash(expanded[expanded.Length - 1])) {
                    return null;
                }
                callArgs = expanded;
            }
            int argumentCount = callArgs.Length;

            var argTypes = new Type[argumentCount];
            for (int i = 0; i < argumentCount; i++) {
                argTypes[i] = (callArgs[i] != null) ? callArgs[i].GetType() : null;
            }

            LibraryCallPlan plan;
            bool[] restricted = null;
            if (method.MethodBases.Length == 1) {
                plan = method.GetCallPlan(0);
                if (plan != null && !plan.AcceptsShape(argumentCount, block)) {
                    plan = null;
                }
                if (plan != null) {
                    // the site's parameter types: Func<CallSite, (scope)?, self, (block)?, arguments..., result>; a splatted list's items
                    // are objects
                    var siteTypes = GetDelegateTypeArguments(delegateType);
                    if (siteTypes.Length != first + count + 2) {
                        return null;
                    }
                    restricted = new bool[argumentCount];
                    for (int i = 0; i < argumentCount; i++) {
                        restricted[i] = plan.RestrictsArgument(i, (i < simpleCount) ? siteTypes[first + 1 + i] : typeof(object));
                    }
                }
            } else {
                // The overload the resolver would pick, if that can be told by the parameter types alone, else asks it. Either way
                // the choice is made for arguments of these types, and holds for a call that passes arguments of the same. A splatted
                // call always asks the resolver, given the call as the rule is (the cache is of calls that splat nothing).
                int index;
                if (splatCount >= 0) {
                    index = Resolve(binder, method, args);
                } else {
                    var selector = method.GetOverloadSelector();
                    index = (selector != null) ? selector.Select(target, signature.HasBlock, block, callArgs) : -1;
                    if (index == -1) {
                        index = method.GetResolvedOverload(binder, target, argTypes, signature.HasBlock, block != null, args);
                    }
                }
                plan = (index >= 0) ? method.GetCallPlan(index) : null;
                if (plan != null && !plan.AcceptsShape(argumentCount, block)) {
                    plan = null;
                }
            }

            if (plan == null || !plan.Accepts(target, signature.HasBlock, block, callArgs)) {
                return null;
            }

            // A site the runtime makes itself (a hook call, say) may have typed parameters:
            bool isUntyped = MethodDispatcher.UntypedFuncs.Contains(delegateType);
            LibraryDispatcher dispatcher;
            if (isUntyped || IsSiteDelegate(delegateType, signature)) {
                dispatcher = CreateDispatcher(count, signature.HasScope, signature.HasBlock);
            } else if ((dispatcher = TryCreateTypedDispatcher(delegateType, count, signature)) == null) {
                return null;
            }

            dispatcher.SetBudget(binder, targetClass.Context);
            dispatcher._context = targetClass.Context;
            dispatcher._plan = plan;
            dispatcher._splatCount = splatCount;
            dispatcher._hasBlock = signature.HasBlock;
            dispatcher._argTypes = argTypes;
            dispatcher._restricted = restricted;
            dispatcher._blockGiven = block != null;
            dispatcher._isRubyObject = isRubyObject;
            dispatcher._targetType = (target != null) ? target.GetType() : null;
            dispatcher._bool = (target is bool) ? (bool)target : false;
            dispatcher._classVersion = targetClass.Version;
            dispatcher._clrTest = (isRubyObject || target == null || target is bool) ? RubyCallAction.ClrReceiverTest.Plain : clrTest;
            dispatcher.Version = version;

            if (methodObject != null) {
                dispatcher.SetMethodObject(methodObject);
            }

            object result = dispatcher.CreateDelegate(isUntyped);
            return (result.GetType() == delegateType) ? result : null;
        }

        // The parameterless construction of a value type with no such constructor (RubyOps.CreateDefaultValue<T>), which the
        // rule builds itself.
        private static bool IsDefaultValueConstruction(System.Reflection.MethodBase method) {
            var info = method as System.Reflection.MethodInfo;
            return info != null && info.IsGenericMethod && info.GetGenericMethodDefinition() == RubyOps.CreateDefaultValueMethod;
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
                IsDefaultValueConstruction(bindingTarget.Overload.ReflectionInfo)) {
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

    public sealed class TypedLibraryDispatcher<TSelf> : LibraryDispatcher where TSelf : class {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, TSelf, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, TSelf self) {
            object result;
            if (CanCall(self) && TryInvoke(GetScope(null), self, null, ArrayUtils.EmptyObjects, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TSelf, object>>)callSite).Update(callSite, self);
        }
    }

    public sealed class TypedLibraryDispatcher<TSelf, T0> : LibraryDispatcher where TSelf : class where T0 : class {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, TSelf, T0, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, TSelf self, T0 arg0) {
            object result;
            if (CanCall(self) && TryInvoke(GetScope(null), self, null, new object[] { arg0 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TSelf, T0, object>>)callSite).Update(callSite, self, arg0);
        }
    }

    public sealed class TypedLibraryDispatcher<TSelf, T0, T1> : LibraryDispatcher where TSelf : class where T0 : class where T1 : class {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, TSelf, T0, T1, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, TSelf self, T0 arg0, T1 arg1) {
            object result;
            if (CanCall(self) && TryInvoke(GetScope(null), self, null, new object[] { arg0, arg1 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TSelf, T0, T1, object>>)callSite).Update(callSite, self, arg0, arg1);
        }
    }

    /// <summary>
    /// The test of a Method object the rule of Method#call makes (RubyMethod.BuildInvoke) - of its method, its name, that it stands
    /// for no method_missing, and of its receiver's exact type - and the receiver test of the call of #call itself (the Method's).
    /// </summary>
    internal sealed class MethodObjectCall {
        private readonly RubyMemberInfo/*!*/ _info;
        private readonly string/*!*/ _name;
        private readonly Type _receiverType;

        internal readonly Type/*!*/ MethodObjectType;
        internal readonly bool IsRubyObject;
        internal readonly RubyClass/*!*/ MethodClass;
        internal readonly RubyCallAction.ClrReceiverTest ClrTest;
        internal readonly int Version;

        internal MethodObjectCall(RubyMethod/*!*/ method, RubyClass/*!*/ methodClass, RubyCallAction.ClrReceiverTest clrTest, int version) {
            _info = method.Info;
            _name = method.Name;
            _receiverType = (method.Target != null) ? method.Target.GetType() : null;
            MethodObjectType = method.GetType();
            IsRubyObject = method is IRubyObject;
            MethodClass = methodClass;
            ClrTest = IsRubyObject ? RubyCallAction.ClrReceiverTest.Plain : clrTest;
            Version = version;
        }

        /// <param name="self">The Method object, of the type tested (the dispatcher's receiver test).</param>
        internal bool TryGetReceiver(object self, out object receiver) {
            var method = (RubyMethod)self;
            receiver = method.Target;
            return ReferenceEquals(method.Info, _info) && method.Name == _name && !method.IsMethodMissing &&
                (receiver != null ? receiver.GetType() == _receiverType : _receiverType == null);
        }
    }

    /// <summary>
    /// The call `obj.send(:name, args)' makes (see DispatchIntrinsics.TryBuildSend): of name, with the arguments after it, the block
    /// and implicit self - so that a private method is called too - through a call site of the dispatcher's own, which is bound as any
    /// site is (a dispatcher for its first calls, a rule once it is hot). The site's signature is the one the rule rewrites the call to
    /// (CallArguments.RemoveSendMethodName). The dispatcher holds for calls that send the same symbol.
    /// </summary>
    internal sealed class SendCall {
        private readonly RubySymbol/*!*/ _symbol;
        private readonly CallSite/*!*/ _site;
        private readonly bool _hasBlock;

        private SendCall(RubySymbol/*!*/ symbol, CallSite/*!*/ site, bool hasBlock) {
            _symbol = symbol;
            _site = site;
            _hasBlock = hasBlock;
        }

        /// <param name="count">The number of arguments after the name.</param>
        internal static SendCall TryCreate(RubyContext/*!*/ context, RubyCallSignature signature, RubySymbol symbol, int count) {
            // the calls TryBuildSend folds:
            if (symbol == null || !signature.HasScope || signature.ResolveOnly || signature.IsVirtualCall || signature.IsSuperCall ||
                signature.IsInteropCall || count > 4) {
                return null;
            }

            var binder = RubyCallAction.Make(context, symbol.ToString(), new RubyCallSignature(count,
                (signature.Flags & ~(RubyCallFlags.HasSourceBlock | RubyCallFlags.IsVariableCall)) | RubyCallFlags.HasImplicitSelf
            ));

            CallSite site;
            if (signature.HasBlock) {
                switch (count) {
                    case 0: site = CallSite<Func<CallSite, RubyScope, object, Proc, object>>.Create(binder); break;
                    case 1: site = CallSite<Func<CallSite, RubyScope, object, Proc, object, object>>.Create(binder); break;
                    case 2: site = CallSite<Func<CallSite, RubyScope, object, Proc, object, object, object>>.Create(binder); break;
                    case 3: site = CallSite<Func<CallSite, RubyScope, object, Proc, object, object, object, object>>.Create(binder); break;
                    default: site = CallSite<Func<CallSite, RubyScope, object, Proc, object, object, object, object, object>>.Create(binder); break;
                }
            } else {
                switch (count) {
                    case 0: site = CallSite<Func<CallSite, RubyScope, object, object>>.Create(binder); break;
                    case 1: site = CallSite<Func<CallSite, RubyScope, object, object, object>>.Create(binder); break;
                    case 2: site = CallSite<Func<CallSite, RubyScope, object, object, object, object>>.Create(binder); break;
                    case 3: site = CallSite<Func<CallSite, RubyScope, object, object, object, object, object>>.Create(binder); break;
                    default: site = CallSite<Func<CallSite, RubyScope, object, object, object, object, object, object>>.Create(binder); break;
                }
            }
            return new SendCall(symbol, site, signature.HasBlock);
        }

        // the rule's test of the name (the symbol is unique in its runtime):
        internal bool Matches(object[]/*!*/ args) {
            return args.Length > 0 && ReferenceEquals(args[0], _symbol);
        }

        /// <param name="args">The send's arguments: the name, then the arguments it passes.</param>
        internal object Invoke(RubyScope/*!*/ scope, object self, Proc block, object[]/*!*/ args) {
            var site = _site;
            if (_hasBlock) {
                switch (args.Length) {
                    case 1: return ((CallSite<Func<CallSite, RubyScope, object, Proc, object>>)site).Target(site, scope, self, block);
                    case 2: return ((CallSite<Func<CallSite, RubyScope, object, Proc, object, object>>)site).Target(site, scope, self, block, args[1]);
                    case 3: return ((CallSite<Func<CallSite, RubyScope, object, Proc, object, object, object>>)site).Target(site, scope, self, block, args[1], args[2]);
                    case 4: return ((CallSite<Func<CallSite, RubyScope, object, Proc, object, object, object, object>>)site).Target(site, scope, self, block, args[1], args[2], args[3]);
                    case 5: return ((CallSite<Func<CallSite, RubyScope, object, Proc, object, object, object, object, object>>)site).Target(site, scope, self, block, args[1], args[2], args[3], args[4]);
                }
            } else {
                switch (args.Length) {
                    case 1: return ((CallSite<Func<CallSite, RubyScope, object, object>>)site).Target(site, scope, self);
                    case 2: return ((CallSite<Func<CallSite, RubyScope, object, object, object>>)site).Target(site, scope, self, args[1]);
                    case 3: return ((CallSite<Func<CallSite, RubyScope, object, object, object, object>>)site).Target(site, scope, self, args[1], args[2]);
                    case 4: return ((CallSite<Func<CallSite, RubyScope, object, object, object, object, object>>)site).Target(site, scope, self, args[1], args[2], args[3]);
                    case 5: return ((CallSite<Func<CallSite, RubyScope, object, object, object, object, object, object>>)site).Target(site, scope, self, args[1], args[2], args[3], args[4]);
                }
            }
            throw Assert.Unreachable;
        }
    }

    /// <summary>
    /// What the rule RubyClass.BuildObjectConstructionNoFlow builds for Class#new of a plain Ruby class does: under the class's
    /// version (its initializer), allocate a RubyObject and call a Ruby initializer that takes the arguments given - or, the
    /// initializer being Object's (or BasicObject's) and there being no arguments, only allocate.
    /// </summary>
    internal sealed class ObjectConstruction {
        private readonly RubyClass/*!*/ _class;
        private readonly int _version;

        // the initializer's body (Func<object, Proc, object x N, object>) of mandatory parameters only, or the call of one with optional
        // or rest parameters; both null if it is not called:
        private readonly Delegate _initializer;
        private readonly RubyMethodCallPlan _initializerCall;
        private readonly int _arity;

        internal ObjectConstruction(RubyClass/*!*/ cls, int version, Delegate initializer, int arity) {
            _class = cls;
            _version = version;
            _initializer = initializer;
            _arity = arity;
        }

        internal ObjectConstruction(RubyClass/*!*/ cls, int version, RubyMethodCallPlan/*!*/ initializerCall, int arity) {
            _class = cls;
            _version = version;
            _initializerCall = initializerCall;
            _arity = arity;
        }

        internal bool CallsInitializer {
            get { return _initializer != null || _initializerCall != null; }
        }

        internal bool IsCurrent(int count) {
            return _class.Version.Method == _version && count == _arity;
        }

        /// <param name="block">The block given to new, passed to the initializer (only a construction that calls one is given one).</param>
        internal object Invoke(object[]/*!*/ args, Proc block) {
            if (block == null) {
                return Construct(args, null);
            }

            // RubyMethodInfo.RuleControlFlowBuilder: a break from the block unwinds to the frame of the call of new, which returns its value
            var rfc = RubyOps.CreateRfcForMethod(block);
            try {
                return Construct(args, block);
            } catch (MethodUnwinder unwinder) when (unwinder.TargetFrame == rfc) {
                return unwinder.ReturnValue;
            } finally {
                RubyOps.LeaveMethodFrame(rfc);
            }
        }

        private object/*!*/ Construct(object[]/*!*/ args, Proc block) {
            // PropagateRetrySingleton(instance = new RubyObject(class), instance.initialize(args)):
            var instance = new RubyObject(_class);
            if (!CallsInitializer) {
                return instance;
            }

            object result;
            if (_initializerCall != null) {
                result = _initializerCall.Invoke(instance, block, args);
            } else {
                switch (args.Length) {
                    case 0: result = ((Func<object, Proc, object>)_initializer)(instance, block); break;
                    case 1: result = ((Func<object, Proc, object, object>)_initializer)(instance, block, args[0]); break;
                    case 2: result = ((Func<object, Proc, object, object, object>)_initializer)(instance, block, args[0], args[1]); break;
                    case 3: result = ((Func<object, Proc, object, object, object, object>)_initializer)(instance, block, args[0], args[1], args[2]); break;
                    case 4: result = ((Func<object, Proc, object, object, object, object, object>)_initializer)(instance, block, args[0], args[1], args[2], args[3]); break;
                    case 5: result = ((Func<object, Proc, object, object, object, object, object, object>)_initializer)(instance, block, args[0], args[1], args[2], args[3], args[4]); break;
                    default: throw Assert.Unreachable;
                }
            }
            return RubyOps.PropagateRetrySingleton(instance, result);
        }
    }
}

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
using System.Collections;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using Range = IronRuby.Builtins.Range;
using System.Reflection;
using IronRuby.Compiler;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Actions.Calls;
using Microsoft.Scripting.Generation;
using System.Numerics;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Runtime.Conversions {
    using Ast = Expression;
    
    public interface IConvertibleRubyMetaObject : IConvertibleMetaObject {
        Convertibility IsConvertibleTo(Type type, bool isExplicit);
    }

    public struct Convertibility {
        public static readonly Convertibility NotConvertible = new Convertibility(false, null);
        public static readonly Convertibility AlwaysConvertible = new Convertibility(true, null);

        public readonly bool IsConvertible;
        public readonly Expression Assumption;

        public Convertibility(bool isConvertible, Expression assumption) {
            IsConvertible = isConvertible;
            Assumption = assumption;
        }
    }

    internal static partial class Converter {
        internal static Convertibility CanConvertFrom(DynamicMetaObject fromArg, Type/*!*/ fromType, Type/*!*/ toType, bool toNotNullable,
            NarrowingLevel level, bool explicitProtocolConversions, bool implicitProtocolConversions) {
            ContractUtils.RequiresNotNull(fromType, "fromType");
            ContractUtils.RequiresNotNull(toType, "toType");

            var metaConvertible = fromArg as IConvertibleMetaObject;
            var rubyMetaConvertible = fromArg as IConvertibleRubyMetaObject;

            //
            // narrowing level 0:
            //

            if (toType == fromType) {
                return Convertibility.AlwaysConvertible;
            }

            if (fromType == typeof(DynamicNull)) {
                if (toNotNullable) {
                    return Convertibility.NotConvertible;
                }

                if (toType.IsGenericType && toType.GetGenericTypeDefinition() == typeof(Nullable<>)) {
                    return Convertibility.AlwaysConvertible;
                }

                if (!toType.IsValueType) {
                    // null convertible to any reference type:
                    return Convertibility.AlwaysConvertible;
                } else if (toType == typeof(bool)) {
                    return Convertibility.AlwaysConvertible;
                } else if (!ProtocolConversionAction.HasDefaultConversion(toType)) {
                    // null not convertible to a value type unless a protocol conversion is allowed:
                    return Convertibility.NotConvertible;
                }
            }

            // blocks:
            if (fromType == typeof(MissingBlockParam)) {
                return new Convertibility(toType == typeof(BlockParam) && !toNotNullable, null);
            }

            if (fromType == typeof(BlockParam) && toType == typeof(MissingBlockParam)) {
                return Convertibility.AlwaysConvertible;
            }

            if (toType.IsAssignableFrom(fromType)) {
                return Convertibility.AlwaysConvertible;
            }

            if (HasImplicitNumericConversion(fromType, toType)) {
                return Convertibility.AlwaysConvertible;
            }

            if (CompilerHelpers.GetImplicitConverter(fromType, toType) != null) {
                return Convertibility.AlwaysConvertible;
            }

            if (rubyMetaConvertible != null) {
                return rubyMetaConvertible.IsConvertibleTo(toType, false);
            } else if (metaConvertible != null) {
                return new Convertibility(metaConvertible.CanConvertTo(toType, false), null);
            }

            //
            // narrowing level 1:
            //

            if (level < NarrowingLevel.One) {
                return Convertibility.NotConvertible;
            }

            if (explicitProtocolConversions && ProtocolConversionAction.HasDefaultConversion(toType)) {
                return Convertibility.AlwaysConvertible;
            }

            //
            // narrowing level 2:
            //

            if (level < NarrowingLevel.Two) {
                return Convertibility.NotConvertible;
            }

            if (HasExplicitNumericConversion(fromType, toType)) {
                return Convertibility.AlwaysConvertible;
            }

            if (CompilerHelpers.GetExplicitConverter(fromType, toType) != null) {
                return Convertibility.AlwaysConvertible;
            }

            if (CompilerHelpers.HasTypeConverter(fromType, toType)) {
                return Convertibility.AlwaysConvertible;
            }

            if (fromType == typeof(char) && toType == typeof(string)) {
                return Convertibility.AlwaysConvertible;
            }

            if (implicitProtocolConversions && IsCollectionConversion(fromType, toType)) {
                return Convertibility.AlwaysConvertible;
            }

            if (IsTypeGroupConversion(fromType, toType)) {
                return Convertibility.AlwaysConvertible;
            }

            if (fromType == typeof(Range) && toType == typeof(System.Range)) {
                return Convertibility.AlwaysConvertible;
            }

            if (toType == typeof(bool)) {
                return Convertibility.AlwaysConvertible;
            }

            if (rubyMetaConvertible != null) {
                return rubyMetaConvertible.IsConvertibleTo(toType, true);
            } else if (metaConvertible != null) {
                return new Convertibility(metaConvertible.CanConvertTo(toType, true), null);
            }

            // 
            // narrowing level 3:
            // 

            if (level < NarrowingLevel.Three) {
                return Convertibility.NotConvertible;
            }

            if (implicitProtocolConversions && ProtocolConversionAction.HasDefaultConversion(toType)) {
                return Convertibility.AlwaysConvertible;
            }

            // A COM object can potentially be converted to the given interface, but might also be not so use this only as the last resort:
            if (TypeUtils.IsComObjectType(fromType) && toType.IsInterface) {
                return Convertibility.AlwaysConvertible;
            }

            return Convertibility.NotConvertible;
        }

        #region Ruby Array and Hash to typed CLR collections

        // A CLR method that takes an int[], an IEnumerable<string> or a Dictionary<string, object> gets a
        // copy of a Ruby Array or Hash, its items converted the way each would be converted as an argument.
        // A Ruby Array already is an IList<object> and a Hash an IDictionary<object, object>; those pass as they are.

        /// <summary>
        /// The item type of a CLR collection a Ruby Array converts to: T[], IEnumerable&lt;T&gt;, ICollection&lt;T&gt;,
        /// IList&lt;T&gt;, IReadOnlyCollection&lt;T&gt;, IReadOnlyList&lt;T&gt; or List&lt;T&gt;. Null for any other type.
        /// </summary>
        internal static Type GetListItemType(Type/*!*/ toType) {
            Type itemType = null;
            if (toType.IsSZArray) {
                itemType = toType.GetElementType();
            } else if (toType.IsGenericType) {
                Type definition = toType.GetGenericTypeDefinition();
                if (definition == typeof(IEnumerable<>) || definition == typeof(ICollection<>) || definition == typeof(IList<>) ||
                    definition == typeof(IReadOnlyCollection<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(List<>)) {
                    itemType = toType.GetGenericArguments()[0];
                }
            }
            return itemType != null && IsConvertibleItemType(itemType) ? itemType : null;
        }

        /// <summary>
        /// The key and value types of a CLR dictionary a Ruby Hash converts to: IDictionary&lt;K, V&gt;,
        /// IReadOnlyDictionary&lt;K, V&gt; or Dictionary&lt;K, V&gt;. Null for any other type.
        /// </summary>
        internal static Type[] GetDictionaryItemTypes(Type/*!*/ toType) {
            if (toType.IsGenericType) {
                Type definition = toType.GetGenericTypeDefinition();
                if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(Dictionary<,>)) {
                    Type[] types = toType.GetGenericArguments();
                    if (IsConvertibleItemType(types[0]) && IsConvertibleItemType(types[1])) {
                        return types;
                    }
                }
            }
            return null;
        }

        private static bool IsConvertibleItemType(Type/*!*/ type) {
            return !type.ContainsGenericParameters && !type.IsByRef && !type.IsPointer && !type.IsByRefLike;
        }

        private static bool IsCollectionConversion(Type/*!*/ fromType, Type/*!*/ toType) {
            return typeof(RubyArray).IsAssignableFrom(fromType) && GetListItemType(toType) != null
                || typeof(Hash).IsAssignableFrom(fromType) && GetDictionaryItemTypes(toType) != null;
        }

        /// <summary>
        /// Converts <paramref name="expr"/> holding a Ruby Array or Hash to a typed CLR collection, or returns null if
        /// that is not a conversion the two types have (see <see cref="IsCollectionConversion"/>).
        /// </summary>
        internal static Expression TryConvertToCollection(Expression/*!*/ expr, Type/*!*/ fromType, Type/*!*/ toType, RubyContext/*!*/ context,
            bool implicitProtocolConversions) {

            if (!implicitProtocolConversions || !IsCollectionConversion(fromType, toType)) {
                return null;
            }

            MethodInfo converter;
            Expression[] args;
            if (typeof(RubyArray).IsAssignableFrom(fromType)) {
                Type itemType = GetListItemType(toType);
                converter = (toType.IsArray ? RubyOps.ConvertToVectorMethod : RubyOps.ConvertToListMethod).MakeGenericMethod(itemType);
                args = new[] {
                    AstUtils.Convert(expr, typeof(IList)),
                    MakeItemConversionSite(context, itemType, implicitProtocolConversions)
                };
            } else {
                Type[] itemTypes = GetDictionaryItemTypes(toType);
                converter = RubyOps.ConvertToDictionaryMethod.MakeGenericMethod(itemTypes);
                args = new[] {
                    AstUtils.Convert(expr, typeof(IDictionary<object, object>)),
                    MakeItemConversionSite(context, itemTypes[0], implicitProtocolConversions),
                    MakeItemConversionSite(context, itemTypes[1], implicitProtocolConversions)
                };
            }
            return AstUtils.Convert(Ast.Call(converter, args), toType);
        }

        private static Expression/*!*/ MakeItemConversionSite(RubyContext/*!*/ context, Type/*!*/ itemType, bool implicitProtocolConversions) {
            var siteType = typeof(CallSite<>).MakeGenericType(typeof(Func<,,>).MakeGenericType(typeof(CallSite), typeof(object), itemType));
            var binder = ProtocolConversionAction.GetConversionAction(context, itemType, implicitProtocolConversions);
            return AstUtils.Constant(siteType.GetMethod("Create", new[] { typeof(CallSiteBinder) }).Invoke(null, new object[] { binder }), siteType);
        }

        #endregion

        #region Type group to module

        // A constant naming both a generic and a non-generic CLR type (System::IComparable is IComparable and
        // IComparable<T>) is a type group. Where a module or class is expected - include System::IComparable,
        // x.is_a?(System::IComparable) - it stands for its non-generic member.

        private static bool IsTypeGroupConversion(Type/*!*/ fromType, Type/*!*/ toType) {
            return typeof(TypeGroup).IsAssignableFrom(fromType) && (toType == typeof(RubyModule) || toType == typeof(RubyClass));
        }

        internal static Expression TryConvertTypeGroup(Expression/*!*/ expr, Type/*!*/ fromType, Type/*!*/ toType, Expression/*!*/ contextExpression) {
            if (!IsTypeGroupConversion(fromType, toType)) {
                return null;
            }
            return AstUtils.Convert(
                Ast.Call(RubyOps.ConvertTypeGroupToModuleMethod, AstUtils.Convert(contextExpression, typeof(RubyContext)), AstUtils.Convert(expr, typeof(TypeGroup))),
                toType
            );
        }

        #endregion

        /// <summary>
        /// A Ruby Range of Integers passed for a System.Range: 1..3 is 1..4, -3..-1 is ^3..^0, 2.. is 2..^0.
        /// </summary>
        internal static Expression TryConvertRange(Expression/*!*/ expr, Type/*!*/ fromType, Type/*!*/ toType) {
            if (fromType != typeof(Range) || toType != typeof(System.Range)) {
                return null;
            }
            return Ast.Call(RubyOps.ConvertToRangeMethod, AstUtils.Convert(expr, typeof(Range)));
        }

        internal static Expression/*!*/ ConvertExpression(Expression/*!*/ expr, Type/*!*/ toType, RubyContext/*!*/ context, Expression/*!*/ contextExpression,
            bool implicitProtocolConversions) {

            return
                ImplicitConvert(expr, expr.Type, toType) ??
                ExplicitConvert(expr, expr.Type, toType) ??
                AstUtils.LightDynamic(ProtocolConversionAction.GetConversionAction(context, toType, implicitProtocolConversions), toType, expr);
        }

        internal static Expression ImplicitConvert(Expression/*!*/ expr, Type/*!*/ fromType, Type/*!*/ toType) {
            expr = AstUtils.Convert(expr, fromType);

            // Index's own implicit conversion rejects a negative int; a Ruby index counts from the end (-1 is ^1):
            if (fromType == typeof(int) && toType == typeof(System.Index)) {
                return Ast.Call(RubyOps.ConvertToIndexMethod, expr);
            }

            if (toType.IsAssignableFrom(fromType)) {
                return AstUtils.Convert(expr, toType);
            }

            if (HasImplicitNumericConversion(fromType, toType)) {
                return Ast.Convert(expr, toType);
            }

            MethodInfo converter = CompilerHelpers.GetImplicitConverter(fromType, toType);
            if (converter != null) {
                return Ast.Call(null, converter, expr);
            }

            return null;
        }

        internal static Expression ExplicitConvert(Expression/*!*/ expr, Type/*!*/ fromType, Type/*!*/ toType) {
            expr = AstUtils.Convert(expr, fromType);

            if (HasExplicitNumericConversion(fromType, toType)) {
                // special cases to mimic Ruby behavior precisely:
                if (fromType == typeof(BigInteger)) {
                    if (toType == typeof(int)) {
                        return Methods.ConvertBignumToFixnum.OpCall(expr);
                    } else if (toType == typeof(double)) {
                        return Methods.ConvertBignumToFloat.OpCall(expr);
                    }
                } else if (fromType == typeof(double) && toType == typeof(int)) {
                    return Methods.ConvertDoubleToFixnum.OpCall(expr);
                } else if (fromType == typeof(long) && toType == typeof(int)) {
                    // A boxed long is an Integer too big for an Int32, so narrowing it is the same
                    // out-of-range condition a bignum hits - a RangeError, not an OverflowException.
                    return Methods.ConvertInt64ToFixnum.OpCall(expr);
                }

                return Ast.ConvertChecked(expr, toType);
            }

            MethodInfo converter = CompilerHelpers.GetExplicitConverter(fromType, toType);
            if (converter != null) {
                return Ast.Call(null, converter, expr);
            }

            if (fromType == typeof(char) && toType == typeof(string)) {
                return Ast.Call(null, fromType.GetMethod("ToString", BindingFlags.Public | BindingFlags.Static), expr);
            }

            if (toType == typeof(bool)) {
                Debug.Assert(fromType != typeof(bool));
                return fromType.IsValueType ? AstUtils.Constant(true) : Ast.NotEqual(expr, AstUtils.Constant(null));
            }

            // TODO:
            //if (TypeConverter...(fromType, toType)) {
            //    return true;
            //}

            return null;
        }

        internal static Candidate PreferConvert(Type t1, Type t2) {
            switch (t1.GetTypeCode()) {
                case TypeCode.SByte:
                    switch (t2.GetTypeCode()) {
                        case TypeCode.Byte:
                        case TypeCode.UInt16:
                        case TypeCode.UInt32:
                        case TypeCode.UInt64:
                            return Candidate.Two;
                        default:
                            return Candidate.Equivalent;
                    }

                case TypeCode.Int16:
                    switch (t2.GetTypeCode()) {
                        case TypeCode.UInt16:
                        case TypeCode.UInt32:
                        case TypeCode.UInt64:
                            return Candidate.Two;
                        default:
                            return Candidate.Equivalent;
                    }

                case TypeCode.Int32:
                    switch (t2.GetTypeCode()) {
                        case TypeCode.UInt32:
                        case TypeCode.UInt64:
                            return Candidate.Two;
                        default:
                            return Candidate.Equivalent;
                    }

                case TypeCode.Int64:
                    switch (t2.GetTypeCode()) {
                        case TypeCode.UInt64:
                            return Candidate.Two;
                        default:
                            return Candidate.Equivalent;
                    }

                case TypeCode.Boolean:
                    if (t2 == typeof(int)) {
                        return Candidate.Two;
                    }
                    return Candidate.Equivalent;

                case TypeCode.Decimal:
                case TypeCode.Double:
                    if (t2 == typeof(BigInteger)) {
                        return Candidate.Two;
                    }
                    return Candidate.Equivalent;

                case TypeCode.Char:
                    if (t2 == typeof(string)) {
                        return Candidate.Two;
                    }
                    return Candidate.Equivalent;
            }
            return Candidate.Equivalent;
        }

        #region Runtime Conversions

        internal static Byte ToByte(int value) {
            if (value >= Byte.MinValue && value <= Byte.MaxValue) {
                return (Byte)value;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::Byte");
        }

        internal static SByte ToSByte(int value) {
            if (value >= SByte.MinValue && value <= SByte.MaxValue) {
                return (SByte)value;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::SByte");
        }

        internal static Int16 ToInt16(int value) {
            if (value >= Int16.MinValue && value <= Int16.MaxValue) {
                return (Int16)value;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::Int16");
        }

        internal static UInt16 ToUInt16(int value) {
            if (value >= UInt16.MinValue && value <= UInt16.MaxValue) {
                return (UInt16)value;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::UInt16");
        }

        internal static UInt32 ToUInt32(int value) {
            if (value >= UInt32.MinValue) {
                return (UInt32)value;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::UInt32");
        }

        internal static UInt32 ToUInt32(BigInteger value) {
            UInt32 result;
            if (value.AsUInt32(out result)) {
                return result;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::UInt32");
        }

        internal static Int64 ToInt64(BigInteger value) {
            Int64 result;
            if (value.AsInt64(out result)) {
                return result;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::Int64");
        }

        internal static UInt64 ToUInt64(int value) {
            if (value >= 0) {
                return (UInt64)value;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::UInt64");
        }

        internal static UInt64 ToUInt64(BigInteger value) {
            UInt64 result;
            if (value.AsUInt64(out result)) {
                return result;
            }
            throw RubyExceptions.CreateRangeError("number too big to convert into System::UInt64");
        }

        #endregion
    }
}

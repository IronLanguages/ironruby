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
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting;
using Microsoft.Scripting.Actions;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Collections.Generic;
using System.Reflection;

namespace IronRuby.Runtime {
    /// <summary>
    /// Class for implementing standard Ruby conversion logic
    /// 
    /// Ruby conversion rules aren't always consistent, but we should try to capture all
    /// common conversion patterns here. They're more likely to be correct than something
    /// created by hand.
    /// </summary>
    public static class Protocols {

        #region Bignum/Fixnum Normalization

        /// <summary>
        /// Converts a BigInteger to int if it is small enough
        /// </summary>
        /// <param name="x">The value to convert</param>
        /// <returns>An int if x is small enough, otherwise x.</returns>
        /// <remarks>
        /// Use this helper to downgrade BigIntegers as necessary.
        /// </remarks>
        public static object/*!*/ Normalize(BigInteger/*!*/ x) {
            int result;
            if (x.AsInt32(out result)) {
                return ScriptingRuntimeHelpers.Int32ToObject(result);
            }
            long wide;
            if (x.AsInt64(out wide)) {
                return wide;
            }
            return x;
        }

        public static object Normalize(long x) {
            if (x >= Int32.MinValue && x <= Int32.MaxValue) {
                return ScriptingRuntimeHelpers.Int32ToObject((int)x);
            } else {
                return x;
            }
        }

        [CLSCompliant(false)]
        public static object Normalize(ulong x) {
            if (x <= Int32.MaxValue) {
                return ScriptingRuntimeHelpers.Int32ToObject((int)x);
            } else if (x <= Int64.MaxValue) {
                return (long)x;
            } else {
                return new BigInteger(x);
            }
        }

        [CLSCompliant(false)]
        public static object Normalize(uint x) {
            if (x <= Int32.MaxValue) {
                return ScriptingRuntimeHelpers.Int32ToObject((int)x);
            } else {
                return (long)x;
            }
        }

        public static object Normalize(decimal x) {
            if (x >= Int32.MinValue && x <= Int32.MaxValue) {
                return ScriptingRuntimeHelpers.Int32ToObject(Decimal.ToInt32(x));
            }
            if (x >= Int64.MinValue && x <= Int64.MaxValue) {
                return Decimal.ToInt64(x);
            }
            return new BigInteger(x);
        }

        public static object Normalize(object x) {
            if (x is BigInteger) {
                return Normalize((BigInteger)x);
            }
            if (x is long) {
                return Normalize((long)x);
            }
            return x;
        }

        public static double ConvertToDouble(RubyContext/*!*/ context, BigInteger/*!*/ bignum) {
            double result = ToDouble(bignum);
            if (Double.IsInfinity(result)) {
                context.ReportWarning("Integer out of Float range", true);
            }
            return result;
        }

        /// <summary>
        /// The double nearest to an integer, and a signed infinity for one too big to hold.
        /// Casting a BigInteger to a double truncates towards zero rather than rounding to
        /// nearest, which is off by an ulp for roughly half of all values wider than the 53 bits
        /// a double keeps.
        /// </summary>
        public static double ToDouble(BigInteger/*!*/ bignum) {
            return (bignum.Sign < 0) ? -ScaleToDouble(BigInteger.Abs(bignum), 0) : ScaleToDouble(bignum, 0);
        }

        /// <summary>
        /// <paramref name="magnitude"/> times two to the <paramref name="exponent"/>, as the
        /// nearest double, with a tie going to the even neighbour - the rounding the hardware
        /// would do. <paramref name="magnitude"/> must not be negative.
        /// </summary>
        public static double ScaleToDouble(BigInteger magnitude, int exponent) {
            Debug.Assert(magnitude.Sign >= 0);

            if (magnitude.IsZero()) {
                return 0.0;
            }

            long bits = (long)magnitude.GetBitLength();
            if (bits > 53) {
                int dropped = (int)(bits - 53);
                BigInteger low = magnitude & ((BigInteger.One << dropped) - BigInteger.One);
                magnitude >>= dropped;
                exponent += dropped;

                int comparedToHalf = low.CompareTo(BigInteger.One << (dropped - 1));
                if (comparedToHalf > 0 || (comparedToHalf == 0 && !magnitude.IsEven)) {
                    magnitude += BigInteger.One;
                }
            }

            // At most 54 bits by here, which a double holds exactly.
            return Math.ScaleB((double)magnitude, exponent);
        }

        #endregion

        #region CastToString, CastToPath, TryCastToString, ConvertToString, ConvertToEncoding

        /// <summary>
        /// Converts an object to string using to_str protocol (<see cref="ConvertToStrAction"/>).
        /// </summary>
        public static MutableString/*!*/ CastToString(ConversionStorage<MutableString>/*!*/ stringCast, object obj) {
            return CastToString(stringCast.GetSite(ConvertToStrAction.Make(stringCast.Context)), obj);
        }

        /// <summary>
        /// Converts an object to string using to_str protocol (<see cref="ConvertToStrAction"/>).
        /// </summary>
        public static MutableString/*!*/ CastToString(CallSite<Func<CallSite, object, MutableString>>/*!*/ toStrSite, object obj) {
            var result = toStrSite.Target(toStrSite, obj);
            if (result == null) {
                throw RubyExceptions.CreateImplicitConversionError("nil", "String");
            }
            return result;
        }

        /// <summary>
        /// Converts an object to string using to_path-to_str protocol.
        /// Protocol:
        /// ? to_path => to_path() and to_str conversion on the result
        /// ? to_str => to_str()
        /// </summary>
        public static MutableString/*!*/ CastToPath(ConversionStorage<MutableString>/*!*/ toPath, object obj) {
            return CastToPath(toPath.GetSite(CompositeConversionAction.Make(toPath.Context, CompositeConversion.ToPathToStr)), obj);
        }

        /// <summary>
        /// Converts an object to string using to_path-to_str protocol.
        /// Protocol:
        /// ? to_path => to_path() and to_str conversion on the result
        /// ? to_str => to_str()
        /// </summary>
        public static MutableString/*!*/ CastToPath(CallSite<Func<CallSite, object, MutableString>>/*!*/ toPath, object obj) {
            MutableString result = toPath.Target(toPath, obj);
            if (result == null) {
                throw RubyExceptions.CreateImplicitConversionError("nil", "String");
            }
            return CheckPath(result);
        }

        /// <summary>
        /// rb_get_path_check: a path must be in an ASCII-compatible encoding (the
        /// separators have to be findable) and must not contain a NUL, which the
        /// operating system would treat as the end of the name.
        /// </summary>
        public static MutableString/*!*/ CheckPath(MutableString/*!*/ path) {
            if (!path.Encoding.IsAsciiIdentity) {
                throw new EncodingCompatibilityError(String.Format(
                    "path name must be ASCII-compatible ({0}): \"{1}\"",
                    path.Encoding.Name, path.ToStringWithEscapedInvalidCharacters(path.Encoding)
                ));
            }

            if (path.IndexOf('\0') >= 0) {
                throw RubyExceptions.CreateArgumentError("path name contains null byte");
            }

            return path;
        }

        /// <summary>
        /// Converts an object to string using try-to_str protocol (<see cref="TryConvertToStrAction"/>).
        /// </summary>
        public static MutableString TryCastToString(ConversionStorage<MutableString>/*!*/ stringTryCast, object obj) {
            var site = stringTryCast.GetSite(TryConvertToStrAction.Make(stringTryCast.Context));
            return site.Target(site, obj);
        }

        /// <summary>
        /// Convert to string using to_s protocol (<see cref="ConvertToSAction"/>).
        /// </summary>
        public static MutableString/*!*/ ConvertToString(ConversionStorage<MutableString>/*!*/ tosConversion, object obj) {
            var site = tosConversion.GetSite(ConvertToSAction.Make(tosConversion.Context));
            return site.Target(site, obj);
        }

        /// <summary>
        /// Converts an object to a string via to_s and catches all exceptions this conversion might throw.
        /// If the string returned by to_s is a binary string, converts it to a UTF16 string using UTF8 encoding and escapes 
        /// all invalid byte sequences.
        /// </summary>
        internal static string/*!*/ ToClrStringNoThrow(RubyContext/*!*/ context, object obj) {
            try {
                MutableString mstr = obj as MutableString;
                if (mstr == null) {
                    var site = context.StringConversionSite;
                    mstr = site.Target(site, obj);
                }
                return mstr.ToStringWithEscapedInvalidCharacters(RubyEncoding.UTF8);
            } catch (Exception e) {
                return String.Format(CultureInfo.CurrentCulture, "<{0}.to_s raised an exception: '{1}'>", obj, e.Message);
            }
        }

        public static RubyEncoding ConvertToEncoding(ConversionStorage<MutableString>/*!*/ toStr, object/*!*/ obj) {
            return obj as RubyEncoding ??
                   toStr.Context.GetRubyEncoding(Protocols.CastToString(toStr, obj));
        }

        #endregion

        #region CastToArray, TryCastToArray, TryConvertToArray, Splat, Options

        public static IList/*!*/ CastToArray(ConversionStorage<IList>/*!*/ arrayCast, object obj) {
            var site = arrayCast.GetSite(ConvertToArrayAction.Make(arrayCast.Context));
            return site.Target(site, obj);
        }

        public static IList TryCastToArray(ConversionStorage<IList>/*!*/ arrayTryCast, object obj) {
            var site = arrayTryCast.GetSite(TryConvertToArrayAction.Make(arrayTryCast.Context));
            return site.Target(site, obj);
        }

        public static IList TryConvertToArray(ConversionStorage<IList>/*!*/ tryToA, object obj) {
            var site = tryToA.GetSite(TryConvertToAAction.Make(tryToA.Context));
            return site.Target(site, obj);
        }

        internal static IList ImplicitTrySplat(RubyContext/*!*/ context, object splattee) {
            var site = context.GetClassOf(splattee).ToImplicitTrySplatSite;
            return site.Target(site, splattee) as IList;
        }

        public static void TryConvertToOptions(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ref IDictionary<object, object> options, ref object param1, ref object param2) {

            if (options == null && param1 != Missing.Value) {
                var toHashSite = toHash.GetSite(TryConvertToHashAction.Make(toHash.Context));
                if (param2 != Missing.Value) {
                    options = toHashSite.Target(toHashSite, param2);
                    if (options != null) {
                        param2 = Missing.Value;
                    }
                } else {
                    options = toHashSite.Target(toHashSite, param1);
                    if (options != null) {
                        param1 = Missing.Value;
                    }
                }
            }
        }

        #endregion

        #region ConvertStringToFloat, ConvertToInteger, CastToInteger, CastToFixnum, CastToUInt32Unchecked, CastToUInt64Unchecked

        public static double ConvertStringToFloat(RubyContext/*!*/ context, MutableString/*!*/ value) {
            return RubyOps.ConvertStringToFloat(context, value.ConvertToString());
        }

        public static IntegerValue ConvertToInteger(ConversionStorage<IntegerValue>/*!*/ integerConversion, object value) {
            var site = integerConversion.GetSite(CompositeConversionAction.Make(integerConversion.Context, CompositeConversion.ToIntToI));
            return site.Target(site, value); 
        }

        public static IntegerValue CastToInteger(ConversionStorage<IntegerValue>/*!*/ integerConversion, object value) {
            var site = integerConversion.GetSite(ConvertToIntAction.Make(integerConversion.Context));
            return site.Target(site, value);
        }

        /// <summary>
        /// The conversion a method name goes through: a Symbol or a String is taken as it is,
        /// anything else is reported as "X is not a symbol nor a string". Not the same as
        /// CastToString - Symbol has no #to_str.
        /// </summary>
        public static string/*!*/ CastToSymbol(ConversionStorage<string>/*!*/ stringCast, object value) {
            // nil reaches the conversion as a plain null reference and comes back as one; MRI
            // reports it like any other wrong type rather than looking a method named null up.
            if (value == null) {
                throw RubyOps.CreateNotSymbolNorStringError(stringCast.Context, null);
            }
            var site = stringCast.GetSite(ConvertToSymbolAction.Make(stringCast.Context));
            return site.Target(site, value);
        }

        public static double CastToFloat(ConversionStorage<double>/*!*/ floatConversion, object value) {
            var site = floatConversion.GetSite(ConvertToFAction.Make(floatConversion.Context));
            return site.Target(site, value);
        }

        public static int CastToFixnum(ConversionStorage<int>/*!*/ conversionStorage, object value) {
            var site = conversionStorage.GetSite(ConvertToFixnumAction.Make(conversionStorage.Context));
            return site.Target(site, value);
        }

        /// <summary>
        /// Like CastToInteger, but converts the result to an unsigned int.
        /// </summary>
        [CLSCompliant(false)]
        public static uint CastToUInt32Unchecked(ConversionStorage<IntegerValue>/*!*/ integerConversion, object obj) {
            if (obj == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion of nil into Integer");
            }

            return CastToInteger(integerConversion, obj).ToUInt32Unchecked();
        }

        /// <summary>
        /// Like CastToInteger, but converts the result to an unsigned int.
        /// </summary>
        [CLSCompliant(false)]
        public static long CastToInt64Unchecked(ConversionStorage<IntegerValue>/*!*/ integerConversion, object obj) {
            if (obj == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion of nil into Integer");
            }

            return CastToInteger(integerConversion, obj).ToInt64();
        }

        #endregion

        #region Compare (<=>), ConvertCompareResult

        /// <summary>
        /// Try to compare the lhs and rhs. Throws and exception if comparison returns null. Returns -1/0/+1 otherwise.
        /// </summary>
        public static int Compare(ComparisonStorage/*!*/ comparisonStorage, object lhs, object rhs) {
            var compare = comparisonStorage.CompareSite;

            var result = compare.Target(compare, lhs, rhs);
            if (result != null) {
                return Protocols.ConvertCompareResult(comparisonStorage, result);
            } else {
                throw RubyExceptions.MakeComparisonError(comparisonStorage.Context, lhs, rhs);
            }
        }

        public static int ConvertCompareResult(ComparisonStorage/*!*/ comparisonStorage, object/*!*/ result) {
            Debug.Assert(result != null);

            // MRI's rb_cmpint(): an Integer result is used as it is, without asking it how it
            // compares to 0. That saves two dynamic calls per comparison, which is most of the
            // cost of #sort, #min, #max and #sort_by on an ordinary array.
            if (result is int) {
                int i = (int)result;
                return i > 0 ? 1 : (i < 0 ? -1 : 0);
            }

            var greaterThanSite = comparisonStorage.GreaterThanSite;
            if (RubyOps.IsTrue(greaterThanSite.Target(greaterThanSite, result, 0))) {
                return 1;
            }

            var lessThanSite = comparisonStorage.LessThanSite;
            if (RubyOps.IsTrue(lessThanSite.Target(lessThanSite, result, 0))) {
                return -1;
            }

            return 0;
        }

        #endregion

        #region IsTrue, IsEqual, RespondTo, Write

        /// <summary>
        /// Protocol for determining truth in Ruby (not null and not false)
        /// </summary>
        public static bool IsTrue(object obj) {
            return (obj is bool) ? (bool)obj == true : obj != null;
        }

        /// <summary>
        /// Protocol for determining value equality in Ruby (uses IsTrue protocol on result of == call)
        /// </summary>
        public static bool IsEqual(BinaryOpStorage/*!*/ equals, object lhs, object rhs) {
            return IsEqual(equals.GetCallSite("=="), lhs, rhs);
        }

        /// <summary>
        /// Protocol for determining value equality in Ruby (uses IsTrue protocol on result of == call)
        /// </summary>
        public static bool IsEqual(CallSite<Func<CallSite, object, object, object>>/*!*/ site, object lhs, object rhs) {
            // check reference equality first:
            if (lhs == rhs) {
                return true;
            }
            return IsTrue(site.Target(site, lhs, rhs));
        }

        public static bool RespondTo(RespondToStorage/*!*/ respondToStorage, object target, string/*!*/ methodName) {
            return RespondTo(respondToStorage.GetCallSite(), respondToStorage.Context, target, methodName);
        }

        public static bool RespondTo(CallSite<Func<CallSite, object, object, object>>/*!*/ respondToSite, RubyContext/*!*/ context, object target, string/*!*/ methodName) {
            return IsTrue(respondToSite.Target(respondToSite, target, context.EncodeIdentifier(methodName)));
        }

        public static void Write(BinaryOpStorage/*!*/ writeStorage, object target, object value) {
            var site = writeStorage.GetCallSite("write");
            site.Target(site, target, value);
        }

        public static int ToHashCode(object hashResult) {
            if (hashResult is int) {
                return (int)hashResult;
            }

            // MRI calls %(number) on the resulting object if it is not Fixnum and takes internal hash code of the result.
            // It seems to be an implementation detail that we don't need to follow exactly.
            if (hashResult is long) {
                return RubyUtils.GetIntegerHashCode((long)hashResult);
            }

            if (hashResult is BigInteger) {
                return RubyUtils.GetIntegerHashCode((BigInteger)hashResult);
            }

            return hashResult == null ? RubyUtils.NilObjectId : ReferenceEqualityComparer<object>.Instance.GetHashCode(hashResult);
        }

        #endregion

        #region Coercion

        /// <summary>
        /// Try to coerce the values of self and other (using other as the target object) then dynamically invoke "&lt;=&gt;".
        /// </summary>
        /// <returns>
        /// Result of &lt;=&gt; on coerced values or <c>null</c> if "coerce" method is not defined, throws a subclass of SystemException, 
        /// or returns something other than a pair of objects.
        /// </returns>
        public static object CoerceAndCompare(
            BinaryOpStorage/*!*/ coercionStorage,
            BinaryOpStorage/*!*/ comparisonStorage, 
            object self, object other) {

            object result;
            // MRI's rb_num_coerce_cmp coerces with err=FALSE: a #coerce that answers nil makes
            // <=> answer nil, where the arithmetic operators raise a coercion TypeError.
            return TryCoerceAndApply(coercionStorage, comparisonStorage, "<=>", self, other, out result, true) ? result : null;
        }

        /// <summary>
        /// Applies given operator on coerced values and converts its result to Ruby truth (using Protocols.IsTrue).
        /// </summary>
        /// <exception cref="ArgumentError">
        /// "coerce" method is not defined, throws a subclass of SystemException, or returns something other than a pair of objects.
        /// </exception>
        public static bool CoerceAndRelate(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ comparisonStorage, 
            string/*!*/ relationalOp, object self, object other) {

            object result;
            // rb_num_coerce_relop also coerces with err=FALSE, and reports a nil from #coerce
            // as the comparison having failed rather than as a coercion TypeError.
            if (TryCoerceAndApply(coercionStorage, comparisonStorage, relationalOp, self, other, out result, true)) {
                return RubyOps.IsTrue(result);
            }

            throw RubyExceptions.MakeComparisonError(coercionStorage.Context, self, other);
        }

        /// <summary>
        /// Applies given operator on coerced values and returns the result.
        /// </summary>
        /// <exception cref="TypeError">
        /// "coerce" method is not defined, throws a subclass of SystemException, or returns something other than a pair of objects.
        /// </exception>
        public static object CoerceAndApply(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpStorage, 
            string/*!*/ binaryOp, object self, object other) {

            object result;
            if (TryCoerceAndApply(coercionStorage, binaryOpStorage, binaryOp, self, other, out result)) {
                return result;
            }

            throw RubyExceptions.MakeCoercionError(coercionStorage.Context, other, self);
        }

        /// <summary>
        /// Applies given operator on coerced values and returns the result.
        /// </summary>
        /// <exception cref="TypeError">
        /// "coerce" method is not defined, throws a subclass of SystemException, or returns something other than a pair of objects.
        /// </exception>
        public static object TryCoerceAndApply(
            BinaryOpStorage/*!*/ coercionStorage,
            BinaryOpStorage/*!*/ binaryOpStorage, string/*!*/ binaryOp,
            object self, object other) {

            if (other == null) {
                return null;
            }

            object result;
            if (TryCoerceAndApply(coercionStorage, binaryOpStorage, binaryOp, self, other, out result)) {
                if (result != null) {
                    return RubyOps.IsTrue(result);
                }
            }
            return null;
        }

        /// <summary>
        /// MRI's rb_num_coerce_bit: the bitwise operators coerce their operand the way the
        /// arithmetic ones do, and then insist that both halves of the coerced pair are Integers.
        /// A Float coerces perfectly well and is still not something to take the bits of, so what
        /// is checked is the pair rather than the operand.
        /// </summary>
        public static object CoerceAndApplyBitwise(BinaryOpStorage/*!*/ coercionStorage,
            BinaryOpStorage/*!*/ binaryOpStorage, string/*!*/ binaryOp, object self, object other) {

            IList pair;
            if (!TryCoerce(coercionStorage, self, other, out pair) || !IsIntegerValue(pair[0]) || !IsIntegerValue(pair[1])) {
                throw RubyExceptions.MakeCoercionError(coercionStorage.Context, other, self);
            }

            var opSite = binaryOpStorage.GetCallSite(binaryOp);
            return opSite.Target(opSite, pair[0], pair[1]);
        }

        private static bool IsIntegerValue(object value) {
            return value is int || value is long || value is BigInteger;
        }

        private static bool TryCoerceAndApply(
            BinaryOpStorage/*!*/ coercionStorage,
            BinaryOpStorage/*!*/ binaryOpStorage, string/*!*/ binaryOp,
            object self, object other, out object result) {

            return TryCoerceAndApply(coercionStorage, binaryOpStorage, binaryOp, self, other, out result, false);
        }

        private static bool TryCoerceAndApply(
            BinaryOpStorage/*!*/ coercionStorage,
            BinaryOpStorage/*!*/ binaryOpStorage, string/*!*/ binaryOp,
            object self, object other, out object result, bool nilMeansNotCoercible) {

            IList coercedValues;
            if (!TryCoerce(coercionStorage, self, other, out coercedValues, nilMeansNotCoercible)) {
                result = null;
                return false;
            }

            var opSite = binaryOpStorage.GetCallSite(binaryOp);
            result = opSite.Target(opSite, coercedValues[0], coercedValues[1]);
            return true;
        }

        /// <summary>
        /// The pair <paramref name="other"/> answers when asked to coerce itself with
        /// <paramref name="self"/>, or false when it has no #coerce at all.
        /// </summary>
        private static bool TryCoerce(BinaryOpStorage/*!*/ coercionStorage, object self, object other, out IList pair) {
            return TryCoerce(coercionStorage, self, other, out pair, false);
        }

        private static bool TryCoerce(BinaryOpStorage/*!*/ coercionStorage, object self, object other, out IList pair, bool nilMeansNotCoercible) {
            // rb_check_funcall: a respond_to? the object defines for itself is asked first, and
            // a false from it means there is no #coerce to call.
            if (HasUserDefinedRespondTo(coercionStorage.Context, other)) {
                var respondTo = coercionStorage.GetCallSite("respond_to?");
                if (!IsTrue(respondTo.Target(respondTo, other, coercionStorage.Context.EncodeIdentifier("coerce")))) {
                    pair = null;
                    return false;
                }
            }

            var coerce = coercionStorage.GetCallSite("coerce", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));

            object coerced;

            try {
                // Swap self and other around to do the coercion.
                coerced = coerce.Target(coerce, other, self);
            } catch (MissingMethodException e) when (IsUndefinedCoerce(coercionStorage.Context, e, other)) {
                // MRI only falls back to "X can't be coerced into Y" / "comparison of X with Y
                // failed" when #coerce is not defined at all. An exception raised *inside* a
                // user-written #coerce propagates to the caller unchanged. IronRuby used to
                // `catch (SystemException)` here, which is the 1.8 behaviour and silently turned
                // every bug in a #coerce into a TypeError.
                pair = null;
                return false;
            }

            // MRI's do_coerce with err=FALSE - the comparison operators - treats a nil from
            // #coerce as "not coercible", so <=> answers nil and < reports a failed comparison.
            // Anything else that is not a pair is a TypeError for every caller, including those.
            if (coerced == null && nilMeansNotCoercible) {
                pair = null;
                return false;
            }

            var coercedValues = coerced as IList;
            if (coercedValues == null || coercedValues.Count != 2) {
                // #coerce exists but answered something that is not a pair. MRI reports that
                // as its own error rather than pretending the operand was not coercible.
                throw RubyExceptions.CreateTypeError("coerce must return [x, y]");
            }

            pair = coercedValues;
            return true;
        }

        /// <summary>
        /// True when <paramref name="e"/> is the NoMethodError produced by the #coerce dispatch
        /// itself failing to find the method on <paramref name="other"/>, as opposed to a
        /// NoMethodError raised from inside a #coerce that does exist.
        /// </summary>
        private static bool HasUserDefinedRespondTo(RubyContext/*!*/ context, object obj) {
            var method = context.GetImmediateClassOf(obj).ResolveMethod("respond_to?", VisibilityContext.AllVisible);
            return method.Found &&
                !(method.Info.DeclaringModule == context.KernelModule && method.Info is RubyLibraryMethodInfo);
        }

        private static bool IsUndefinedCoerce(RubyContext/*!*/ context, MissingMethodException/*!*/ e, object other) {
            var data = RubyExceptionData.TryGetInstance(e);
            if (data == null) {
                return false;
            }

            var name = data.Name as RubySymbol;
            if (name == null || name.ToString() != "coerce") {
                return false;
            }

            // A boxed value type can be re-boxed between the call site and the error, so the
            // receiver is compared by value as well as by reference.
            return !data.HasReceiver || ReferenceEquals(data.Receiver, other) || Equals(data.Receiver, other);
        }
    
        #endregion

        #region CLR Types

        public static Type[]/*!*/ ToTypes(RubyContext/*!*/ context, object[]/*!*/ values) {
            Type[] args = new Type[values.Length];
            for (int i = 0; i < args.Length; i++) {
                args[i] = ToType(context, values[i]);
            }

            return args;
        }

        public static Type/*!*/ ToType(RubyContext/*!*/ context, object value) {
            TypeTracker tt = value as TypeTracker;
            if (tt != null) {
                return tt.Type;
            }

            RubyModule module = value as RubyModule;
            if (module != null && (module.IsClass || module.IsClrModule)) {
                return module.GetUnderlyingSystemType();
            }

            throw RubyExceptions.InvalidValueForType(context, value, "Class");
        }

        #endregion

        #region Security

        public static void CheckSafeLevel(RubyContext/*!*/ context, int level) {
            if (level <= context.CurrentSafeLevel) {
                throw RubyExceptions.CreateSecurityError("Insecure operation at level " + context.CurrentSafeLevel);
            }
        }
        public static void CheckSafeLevel(RubyContext/*!*/ context, int level, string/*!*/ method) {
            if (level <= context.CurrentSafeLevel) {
                throw RubyExceptions.CreateSecurityError(String.Format("Insecure operation {0} at level {1}", method, context.CurrentSafeLevel));
            }
        }

        #endregion
    }
}

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
using System.Security.Cryptography;

using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    [RubyModule("Kernel", Extends = typeof(Kernel))]
    public static class KernelOps {
        #region initialize_copy

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static object InitializeCopy(RubyContext/*!*/ context, object self, object source) {
            // MRI's order (rb_obj_init_copy): copying an object onto itself is nothing to do, even
            // frozen; otherwise a frozen receiver is refused before the classes are compared.
            if (ReferenceEquals(self, source) || RubyUtils.IsRubyValueType(self) && Equals(self, source)) {
                return self;
            }

            if (context.IsObjectFrozen(self)) {
                throw RubyExceptions.CreateObjectFrozenError(context, self);
            }

            RubyClass selfClass = context.GetClassOf(self);
            RubyClass sourceClass = context.GetClassOf(source);
            if (sourceClass != selfClass) {
                throw RubyExceptions.CreateTypeError("initialize_copy should take same class object");
            }

            return self;
        }


        /// <summary>
        /// #dup and #clone dispatch through these rather than calling #initialize_copy
        /// themselves, so that a class can do different work for the two. The defaults
        /// just forward, which is what MRI's Kernel#initialize_dup does.
        /// </summary>
        [RubyMethod("initialize_dup", RubyMethodAttributes.PrivateInstance)]
        public static object InitializeDuplicate(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage, object self, object source) {

            var site = initializeCopyStorage.GetCallSite("initialize_copy", 1);
            site.Target(site, self, source);
            return self;
        }

        [RubyMethod("initialize_clone", RubyMethodAttributes.PrivateInstance)]
        public static object InitializeClone(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage, object self, object source,
            [Keywords, Optional]object options) {

            var site = initializeCopyStorage.GetCallSite("initialize_copy", 1);
            site.Target(site, self, source);
            return self;
        }

        #endregion

        #region Array, Float, Integer, String, Complex, Rational

        [RubyMethod("Array", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("Array", RubyMethodAttributes.PublicSingleton)]
        public static IList/*!*/ ToArray(ConversionStorage<IList>/*!*/ tryToAry, ConversionStorage<IList>/*!*/ tryToA, object self, object obj) {
            IList result = Protocols.TryCastToArray(tryToAry, obj);
            if (result != null) {
                return result;
            }

            result = Protocols.TryConvertToArray(tryToA, obj);
            if (result != null) {
                return result;
            }

            result = new RubyArray();
            if (obj != null) {
                result.Add(obj);
            }
            return result;
        }

        #region Kernel#Integer, Kernel#Float

        // The Float loop of Numeric#step and Range#step (ArithmeticSequence.__step_each__ in
        // ruby4.rb, after MRI's ruby_float_step): n values beg, beg + step, ..., the last one held
        // to stop. A method that yields is not one the method JIT takes, so the loop cost ~0.25 us
        // a value in Ruby.
        [RubyMethod("__ir_float_step__", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("__ir_float_step__", RubyMethodAttributes.PublicSingleton)]
        public static object FloatStep(BlockParam/*!*/ block, object self, double beg, double stop, double step, double n) {
            object result;
            return FloatStepLoop(block, beg, stop, step, n, out result) ? result : null;
        }

        /// <summary>The loop of <see cref="FloatStep"/>: true when the block broke out, with what it broke with.</summary>
        internal static bool FloatStepLoop(BlockParam/*!*/ block, double beg, double stop, double step, double n, out object result) {
            result = null;
            if (Double.IsInfinity(step)) {
                if (n > 0 && block.Yield(beg, out result)) {
                    return true;
                }
            } else if (step == 0) {
                while (true) {
                    if (block.Yield(beg, out result)) {
                        return true;
                    }
                }
            } else {
                // n may be NaN (Infinity.step(Infinity, 1)); 0 < NaN is false, so nothing is
                // yielded, which is what MRI's for(;i<n;) loop does.
                for (double i = 0; i < n; i++) {
                    double d = i * step + beg;
                    if (step >= 0 ? stop < d : d < stop) {
                        d = stop;
                    }
                    if (block.Yield(d, out result)) {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// MRI's ruby_float_step_size: how many values beg, beg + unit, ... reach end (excluded when
        /// <paramref name="excl"/>), allowing for the error the division accumulates. A Float, so that
        /// an infinite count and a NaN survive. ArithmeticSequence.__float_step_size__ in ruby4.rb is
        /// the same computation.
        /// </summary>
        // C's DBL_EPSILON (Double.Epsilon is the smallest subnormal instead)
        private const double DblEpsilon = 2.220446049250313e-16;

        internal static double FloatStepSize(double beg, double end, double unit, bool excl) {
            if (unit == 0) {
                return Double.PositiveInfinity;
            }
            if (Double.IsInfinity(unit)) {
                return (unit > 0 ? beg <= end : beg >= end) ? 1 : 0;
            }
            double n = (end - beg) / unit;
            double err = (Math.Abs(beg) + Math.Abs(end) + Math.Abs(end - beg)) / Math.Abs(unit) * DblEpsilon;
            if (err > 0.5) {
                err = 0.5;
            }
            double d;
            if (excl) {
                if (n <= 0) {
                    return 0;
                }
                n = (n < 1) ? 0 : Math.Floor(n - err);
                d = +((n + 1) * unit) + beg;
                if (beg < end) {
                    if (d < end) {
                        n++;
                    }
                } else if (beg > end) {
                    if (d > end) {
                        n++;
                    }
                }
            } else {
                if (n < 0) {
                    return 0;
                }
                n = Math.Floor(n + err);
                d = +((n + 1) * unit) + beg;
                if (beg < end) {
                    if (d <= end) {
                        n++;
                    }
                } else if (beg > end) {
                    if (d >= end) {
                        n++;
                    }
                }
            }
            return n + 1;
        }

        // Integer(string), Integer(fixnum) and Integer(float), the common calls, without the storages and the keyword parameter of the
        // general overload below: the binder's dispatch to that one cost more than the conversion (~100 ns for a Fixnum).
        [RubyMethod("Integer", RubyMethodAttributes.PrivateInstance)]
        public static object ToInteger(object self, [NotNull]MutableString/*!*/ value) {
            return ParseInteger(value, 0, true);
        }

        [RubyMethod("Integer", RubyMethodAttributes.PrivateInstance)]
        public static object ToInteger(object self, int value) {
            return ScriptingRuntimeHelpers.Int32ToObject(value);
        }

        [RubyMethod("Integer", RubyMethodAttributes.PrivateInstance)]
        public static object ToInteger(object self, double value) {
            return ClrFloat.ToInt(value);
        }

        /// <summary>
        /// Kernel#Integer(arg, base = 0, exception: true), MRI's rb_convert_to_integer. A non-zero base
        /// goes with a String, so anything else must have #to_str. A String is parsed, an Integer is
        /// answered as it is, a Float truncated and nil is a TypeError. Anything else is asked for #to_int
        /// - an error it raises, or an answer that is not an Integer, only moves on to the next step -
        /// then for #to_str, then for #to_i. With exception: false a failure answers nil, including an
        /// error raised by the object's own #to_i; an unusable base or a String in an ASCII-incompatible
        /// encoding still raises.
        /// </summary>
        [RubyMethod("Integer", RubyMethodAttributes.PrivateInstance)]
        public static object ToInteger(ConversionStorage<int>/*!*/ fixnumCast, UnaryOpStorage/*!*/ toInt, UnaryOpStorage/*!*/ toStr,
            UnaryOpStorage/*!*/ toI, object self, object value, [Optional]object radixOrOptions, [Keywords, Optional]object options) {

            // the common cases, without the keyword and protocol machinery
            if (radixOrOptions == Missing.Value) {
                if (value is MutableString str) {
                    return ParseInteger(str, 0, true);
                }
                if (value is int || value is long || value is BigInteger) {
                    return value;
                }
            }

            bool raise = TakeExceptionOption(fixnumCast.Context, ref radixOrOptions, options, 2);
            if (radixOrOptions == null) {
                // a base given as nil is not a base left out (MRI's NUM2INT), whatever exception: says
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }
            int radix = (radixOrOptions == Missing.Value) ? 0 : Protocols.CastToFixnum(fixnumCast, radixOrOptions);
            RubyContext context = fixnumCast.Context;

            if (radix != 0) {
                MutableString str = TryConvertToStr(toStr, value);
                if (str == null) {
                    if (!raise) {
                        return null;
                    }
                    throw RubyExceptions.CreateArgumentError("base specified for non string value");
                }
                value = str;
            }

            if (value is double d) {
                if (!raise && (Double.IsNaN(d) || Double.IsInfinity(d))) {
                    return null;
                }
                return ClrFloat.ToInt(d);
            }
            object clrInteger = AsInteger(value);
            if (clrInteger != null) {
                return clrInteger;
            }
            if (value is MutableString s) {
                return ParseInteger(s, radix, raise);
            }
            if (value == null) {
                if (!raise) {
                    return null;
                }
                throw RubyExceptions.CreateTypeConversionError("NilClass", "Integer");
            }

            // rb_protect(rb_check_to_int)
            if (CanCall(context, value, "to_int")) {
                try {
                    object integer = AsInteger(Call(toInt, value, "to_int"));
                    if (integer != null) {
                        return integer;
                    }
                } catch (Exception e) when (IsStandardError(context, e)) {
                    // MRI forgets it and goes on to #to_str
                }
            }

            MutableString text = TryConvertToStr(toStr, value);
            if (text != null) {
                return ParseInteger(text, radix, raise);
            }

            if (!raise) {
                try {
                    return CanCall(context, value, "to_i") ? AsInteger(Call(toI, value, "to_i")) : null;
                } catch (Exception e) when (IsStandardError(context, e)) {
                    return null;
                }
            }

            string className = RubyExceptions.MessageTypeName(context.GetClassName(value));
            if (!CanCall(context, value, "to_i")) {
                throw RubyExceptions.CreateTypeConversionError(className, "Integer");
            }
            object result = Call(toI, value, "to_i");
            if (AsInteger(result) != null) {
                return AsInteger(result);
            }
            throw RubyExceptions.CreateTypeError("can't convert {0} to Integer ({0}#to_i gives {1})",
                className, RubyExceptions.MessageTypeName(context.GetClassName(result)));
        }

        /// <summary>
        /// Kernel#Float(arg, exception: true), MRI's rb_convert_to_float. A Float is its own answer - the
        /// very object, so a NaN or an Infinity stays itself - an Integer is converted and a String
        /// parsed. nil, true and false are TypeErrors; anything else is asked for #to_f, whose answer is
        /// handed back as it is if it is a Float. With exception: false a failure answers nil, including
        /// an error raised by the object's own #to_f.
        /// </summary>
        // Kernel.Integer and Kernel.Float: methods of their own, which a backtrace names "Kernel.Integer" as MRI's does
        // (see Format and SingletonFormat).
        [RubyMethod("Integer", RubyMethodAttributes.PublicSingleton)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("ToInteger")]
        public static object SingletonToInteger(ConversionStorage<int>/*!*/ fixnumCast, UnaryOpStorage/*!*/ toInt, UnaryOpStorage/*!*/ toStr,
            UnaryOpStorage/*!*/ toI, object self, object value, [Optional]object radixOrOptions, [Keywords, Optional]object options) {
            return ToInteger(fixnumCast, toInt, toStr, toI, self, value, radixOrOptions, options);
        }

        [RubyMethod("Float", RubyMethodAttributes.PublicSingleton)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("ToFloat")]
        public static object SingletonToFloat(UnaryOpStorage/*!*/ toF, object self, object value, [Keywords, Optional]object options) {
            return ToFloat(toF, self, value, options);
        }

        [RubyMethod("Float", RubyMethodAttributes.PrivateInstance)]
        public static object ToFloat(UnaryOpStorage/*!*/ toF, object self, object value, [Keywords, Optional]object options) {
            // the options are checked first, even for a Float: Float(1.0, foo: 1) raises
            RubyContext context = toF.Context;
            bool raise = (options == Missing.Value) || TakeExceptionOption(context, ref options, Missing.Value, 1);

            if (value is double) {
                return value;
            }

            if (value is int i) {
                return (double)i;
            }
            if (value is BigInteger big) {
                return Protocols.ConvertToDouble(context, big);
            }
            if (value is MutableString str) {
                RequireAsciiCompatible(str);
                if (str.IndexOf('\0') >= 0) {
                    return raise ? throw RubyExceptions.CreateArgumentError("string for Float contains null byte") : null;
                }
                if (!raise) {
                    double parsed;
                    return RubyOps.TryConvertKernelFloat(context, str, out parsed) ? (object)parsed : null;
                }
                return RubyOps.ConvertKernelFloat(context, str);
            }
            if (value == null || value is bool) {
                if (!raise) {
                    return null;
                }
                throw RubyExceptions.CreateTypeConversionError(context.GetClassName(value), "Float");
            }

            if (!CanCall(context, value, "to_f")) {
                if (!raise) {
                    return null;
                }
                throw RubyExceptions.CreateTypeConversionError(context.GetClassName(value), "Float");
            }

            object result;
            try {
                result = Call(toF, value, "to_f");
            } catch (Exception e) when (!raise && IsStandardError(context, e)) {
                return null;
            }
            if (result is double) {
                return result;
            }
            if (!raise) {
                return null;
            }
            throw RubyExceptions.CreateTypeError("can't convert {0} to Float ({0}#to_f gives {1})",
                context.GetClassName(value), context.GetClassName(result));
        }

        /// <summary>
        /// The exception: keyword of Integer() and Float(), which arrives in place of the first optional
        /// argument that was not given (<paramref name="arg"/>, then <paramref name="options"/>), as a Hash
        /// flagged as keywords. MRI's rb_bool_expected: only true and false are accepted.
        /// </summary>
        private static bool TakeExceptionOption(RubyContext/*!*/ context, ref object arg, object options, int maxArgs) {
            Hash keywords;
            if (options != Missing.Value) {
                keywords = options as Hash;
                if (keywords == null || !keywords.IsKeywordArguments) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..{1})", maxArgs + 1, maxArgs);
                }
            } else if ((keywords = arg as Hash) != null && keywords.IsKeywordArguments) {
                arg = Missing.Value;
            } else if (maxArgs == 1 && arg != Missing.Value) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 2, expected 1)");
            } else {
                return true;
            }

            bool raise = true;
            foreach (var entry in keywords) {
                var key = entry.Key as RubySymbol;
                if (key == null || key.ToString() != "exception") {
                    throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(entry.Key));
                }
                if (!(entry.Value is bool flag)) {
                    throw RubyExceptions.CreateArgumentError("expected true or false as exception: {0}", context.Inspect(entry.Value));
                }
                raise = flag;
            }
            return raise;
        }

        /// <summary>
        /// <paramref name="value"/> as a Ruby Integer: an Integer itself - a Fixnum, an Int64 or a Bignum - and
        /// one of the CLR's other integer types widened to one; null for anything else.
        /// </summary>
        private static object AsInteger(object value) {
            switch (value) {
                case int _:
                case long _:
                case BigInteger _: return value;
                case byte b: return (int)b;
                case sbyte sb: return (int)sb;
                case short sh: return (int)sh;
                case ushort us: return (int)us;
                case uint ui: return Protocols.Normalize(ui);
                case ulong ul: return Protocols.Normalize(ul);
                default: return null;
            }
        }

        /// <summary>
        /// MRI's rb_check_funcall test: the object answers <paramref name="name"/>, privately or not.
        /// A respond_to? of its own is asked; otherwise the method is looked up, and a
        /// respond_to_missing? of its own is asked when there is none.
        /// </summary>
        private static bool CanCall(RubyContext/*!*/ context, object value, string/*!*/ name) {
            RubyClass cls = context.GetImmediateClassOf(value);
            var respondTo = cls.ResolveMethod("respond_to?", VisibilityContext.AllVisible);
            if (respondTo.Found && !(respondTo.Info is RubyLibraryMethodInfo)) {
                return context.RespondTo(value, name);
            }
            if (cls.ResolveMethod(name, VisibilityContext.AllVisible).Found) {
                return true;
            }
            var missing = cls.ResolveMethod("respond_to_missing?", VisibilityContext.AllVisible);
            return respondTo.Found && missing.Found && !(missing.Info is RubyLibraryMethodInfo) && context.RespondTo(value, name);
        }

        /// <summary>A call of a conversion method that may be private, as MRI's rb_check_funcall makes it.</summary>
        private static object Call(UnaryOpStorage/*!*/ storage, object value, string/*!*/ name) {
            var site = storage.GetCallSite(name, new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf));
            return site.Target(site, value);
        }

        /// <summary>
        /// MRI's rb_check_string_type: the String itself, the result of #to_str, or null when there is
        /// no #to_str; a #to_str that answers anything else is a TypeError.
        /// </summary>
        private static MutableString TryConvertToStr(UnaryOpStorage/*!*/ toStr, object value) {
            if (value is MutableString str) {
                return str;
            }
            RubyContext context = toStr.Context;
            if (value == null || !CanCall(context, value, "to_str")) {
                return null;
            }
            object result = Call(toStr, value, "to_str");
            if (result is MutableString || result == null) {
                return (MutableString)result;
            }
            string className = RubyExceptions.MessageTypeName(context.GetClassName(value));
            throw RubyExceptions.CreateTypeError("can't convert {0} to String ({0}#to_str gives {1})",
                className, RubyExceptions.MessageTypeName(context.GetClassName(result)));
        }

        private static bool IsStandardError(RubyContext/*!*/ context, Exception/*!*/ e) {
            return context.GetClassOf(e).IsSubclassOf(context.StandardErrorClass);
        }

        private static void RequireAsciiCompatible(MutableString/*!*/ value) {
            if (!value.Encoding.IsAsciiIdentity) {
                throw new EncodingCompatibilityError("ASCII incompatible encoding: " + value.Encoding.Name);
            }
        }

        /// <summary>
        /// Kernel#Integer's reading of a String, which must be ASCII-compatible. A positive radix must be
        /// 2..36; a negative one is only a hint a prefix overrides, and 0 works it out from the prefix.
        /// A string that does not read as an Integer is an ArgumentError, or null when
        /// <paramref name="raise"/> is false.
        /// </summary>
        public static object ParseInteger(MutableString/*!*/ value, int radix, bool raise = true) {
            RequireAsciiCompatible(value);
            var str = value.ConvertToString();

            // MRI complains about the string before it complains about the radix, so
            // Integer("\n", 1) is "invalid value", not "invalid radix".
            if (!HasIntegerDigits(str)) {
                return raise ? throw InvalidIntegerValue(value) : null;
            }

            // Only a positive radix is range-checked; a negative one is a hint, and an
            // unusable magnitude just falls back to 10.
            if (radix > 0 && (radix < 2 || radix > 36)) {
                throw RubyExceptions.CreateArgumentError("invalid radix {0}", radix);
            }

            object result;
            if (TryParseRubyInteger(str, radix, out result)) {
                return result;
            }
            return raise ? throw InvalidIntegerValue(value) : null;
        }

        #endregion
        #region Kernel#Integer string grammar

        // Kernel#Integer is stricter than String#to_i: it consumes the whole string or fails.
        // Surrounding whitespace and one sign are allowed, an embedded NUL is not; a radix
        // prefix of 0x, 0b, 0o or 0d may appear, and a bare leading zero means octal; single
        // underscores may separate digits. Derived by differential testing against CRuby 3.3.8.

        /// <summary>True when the string holds anything but surrounding whitespace.</summary>
        private static bool HasIntegerDigits(string/*!*/ str) {
            foreach (char c in str) {
                if (!IsIntegerWhitespace(c)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// MRI quotes the offending string as #inspect does: control characters and bytes that
        /// are not valid in its encoding escaped ("1\xFF"), everything else passed through.
        /// </summary>
        private static Exception/*!*/ InvalidIntegerValue(MutableString/*!*/ value) {
            return RubyExceptions.CreateArgumentError("invalid value for Integer(): {0}",
                MutableStringOps.GetQuotedStringRepresentation(value, false, '"'));
        }

        private static bool IsIntegerWhitespace(char c) {
            return c == ' ' || (c >= '\t' && c <= '\r');
        }

        private static int DigitValue(char c) {
            if (c >= '0' && c <= '9') {
                return c - '0';
            }
            if (c >= 'a' && c <= 'z') {
                return c - 'a' + 10;
            }
            if (c >= 'A' && c <= 'Z') {
                return c - 'A' + 10;
            }
            return -1;
        }

        private static bool TryParseRubyInteger(string/*!*/ str, int requestedRadix, out object result) {
            result = null;

            int index = 0;
            int end = str.Length;
            while (index < end && IsIntegerWhitespace(str[index])) {
                index++;
            }
            while (end > index && IsIntegerWhitespace(str[end - 1])) {
                end--;
            }
            if (index == end) {
                return false;
            }

            bool negative = false;
            if (str[index] == '+' || str[index] == '-') {
                negative = (str[index] == '-');
                index++;
            }

            // requestedRadix 0 (or absent) means "infer from the prefix"; a negative one
            // means "prefer the prefix, fall back to |radix|".
            bool inferRadix = requestedRadix <= 0;
            int radix = inferRadix ? 0 : requestedRadix;
            int digits = 0;

            if (index < end && str[index] == '0') {
                char prefix = (index + 1 < end) ? str[index + 1] : '\0';
                int prefixRadix;
                switch (prefix) {
                    case 'x': case 'X': prefixRadix = 16; break;
                    case 'b': case 'B': prefixRadix = 2; break;
                    case 'o': case 'O': prefixRadix = 8; break;
                    case 'd': case 'D': prefixRadix = 10; break;
                    default: prefixRadix = 0; break;
                }

                if (prefixRadix != 0 && (inferRadix || radix == prefixRadix)) {
                    radix = prefixRadix;
                    index += 2;
                } else if (prefixRadix != 0) {
                    // A prefix that contradicts the radix is not a prefix: in base 36
                    // "0x1f" is four ordinary digits, in base 10 it is an error, and the
                    // digit loop below decides which.
                } else if (inferRadix) {
                    // A bare leading zero is octal, and counts as a digit in its own right so
                    // that "0" and "0_0" parse while "08" does not.
                    radix = 8;
                    digits = 1;
                    index++;
                }
                // With an explicit radix a leading zero is just another digit.
            }

            if (radix <= 0) {
                int hinted = -requestedRadix;
                radix = (requestedRadix < 0 && hinted >= 2 && hinted <= 36) ? hinted : 10;
            }

            BigInteger magnitude = BigInteger.Zero;
            while (index < end) {
                char c = str[index];
                int digit = DigitValue(c);
                if (digit >= 0 && digit < radix) {
                    magnitude = magnitude * radix + digit;
                    digits++;
                    index++;
                } else if (c == '_' && digits > 0) {
                    int next = (index + 1 < end) ? DigitValue(str[index + 1]) : -1;
                    if (next < 0 || next >= radix) {
                        return false;
                    }
                    index++;
                } else {
                    return false;
                }
            }

            if (digits == 0) {
                return false;
            }

            result = Protocols.Normalize(negative ? -magnitude : magnitude);
            return true;
        }

        #endregion

        [RubyMethod("String", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("String", RubyMethodAttributes.PublicSingleton)]
        public static object/*!*/ ToString(ConversionStorage<MutableString>/*!*/ tosConversion, object self, object obj) {
            return Protocols.ConvertToString(tosConversion, obj);
        }

        // How the C# library makes a Rational of two Integers it has no RubyRational for yet:
        // Kernel.Rational(numerator, denominator).
        internal static object/*!*/ ToRational(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, object numerator, object denominator) {

            var site = toRational.GetCallSite("Rational", 2);
            return site.Target(site, scope.RubyContext.KernelModule, numerator, denominator);
        }

        // Kernel#Rational(x, y = <none>, exception: true) (MRI's nurat_f_rational) and Kernel#Complex (nucomp_f_complex).
        // The cases a program spends its time in are answered here - an Integer over an Integer, a Rational alone, an
        // Integer or Float pair - and everything else goes to the prelude's port of nurat_convert/nucomp_convert
        // (Rational.__convert__ in rational.rb, Complex.__convert__ in complex.rb), which knows Strings, Floats, the
        // #to_r/#to_c protocol and its errors. Rational(x) and Rational(x, nil) are different calls - the second is a
        // TypeError - so the denominator is optional rather than defaulted. The exception: keyword arrives in place of
        // the first optional argument that was not given (TakeExceptionOption).

        [RubyMethod("Rational", RubyMethodAttributes.PrivateInstance)]
        public static object ToRational(CallSiteStorage<Func<CallSite, object, object, object, object, object, object>>/*!*/ convert,
            RubyContext/*!*/ context, object self, object value, [Optional]object denominator, [Keywords, Optional]object options) {

            RubyClass cls = GetRationalClass(context);
            if (options == Missing.Value && cls != null) {
                if (denominator == Missing.Value) {
                    if (RubyRational.IsIntegerValue(value)) {
                        return new RubyRational(cls, value, ScriptingRuntimeHelpers.Int32ToObject(1));
                    }
                    if (value is RubyRational) {
                        return value;
                    }
                } else if (RubyRational.IsIntegerValue(value) && RubyRational.IsIntegerValue(denominator)) {
                    return RubyRational.Canonicalize(cls, value, denominator);
                }
            }

            bool raise = TakeExceptionOption(context, ref denominator, options, 2);
            bool given2 = denominator != Missing.Value;
            var site = convert.GetCallSite("__convert__", 4);
            return site.Target(site, context.GetClass(typeof(RubyRational)), value, given2 ? denominator : null, given2, raise);
        }

        // Kernel.Rational and Kernel.Complex: methods of their own, which a backtrace names "Kernel.Rational" as MRI's does.
        [RubyMethod("Rational", RubyMethodAttributes.PublicSingleton)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("ToRational")]
        public static object SingletonToRational(CallSiteStorage<Func<CallSite, object, object, object, object, object, object>>/*!*/ convert,
            RubyContext/*!*/ context, object self, object value, [Optional]object denominator, [Keywords, Optional]object options) {
            return ToRational(convert, context, self, value, denominator, options);
        }

        private static bool IsIntegerOrFloat(object value) {
            return value is int || value is long || value is double;
        }

        [RubyMethod("Complex", RubyMethodAttributes.PrivateInstance)]
        public static object ToComplex(CallSiteStorage<Func<CallSite, object, object, object, object, object, object>>/*!*/ convert,
            RubyContext/*!*/ context, object self, object real, [Optional]object imaginary, [Keywords, Optional]object options) {

            RubyClass cls = context.GetClass(typeof(RubyComplex));
            if (options == Missing.Value) {
                if (imaginary == Missing.Value) {
                    if (IsIntegerOrFloat(real)) {
                        return new RubyComplex(cls, real, ScriptingRuntimeHelpers.Int32ToObject(0));
                    }
                    // Complex(c) answers a Complex as it is
                    if (real is RubyComplex && context.GetClassOf(real) == cls) {
                        return real;
                    }
                } else if (IsIntegerOrFloat(real) && IsIntegerOrFloat(imaginary)) {
                    return new RubyComplex(cls, real, imaginary);
                }
            }

            bool raise = TakeExceptionOption(context, ref imaginary, options, 2);
            if (imaginary == Missing.Value && real is RubyComplex && context.GetClassOf(real) == cls) {
                return real;
            }
            bool given2 = imaginary != Missing.Value;
            var site = convert.GetCallSite("__convert__", 4);
            return site.Target(site, cls, real, given2 ? imaginary : null, given2, raise);
        }

        [RubyMethod("Complex", RubyMethodAttributes.PublicSingleton)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("ToComplex")]
        public static object SingletonToComplex(CallSiteStorage<Func<CallSite, object, object, object, object, object, object>>/*!*/ convert,
            RubyContext/*!*/ context, object self, object real, [Optional]object imaginary, [Keywords, Optional]object options) {
            return ToComplex(convert, context, self, real, imaginary, options);
        }

        // The Rational class of the context that last asked (one per process in practice).
        private static RubyClass _rationalClass;

        internal static RubyClass GetRationalClass(RubyContext/*!*/ context) {
            RubyClass cls = _rationalClass;
            if (cls != null && cls.Context == context) {
                return cls;
            }
            object value;
            cls = context.ObjectClass.TryGetConstant(null, "Rational", out value) ? value as RubyClass : null;
            // the Rational class, whose instances are RubyRationals
            if (cls == null || cls.IsSingletonClass || cls.Name != "Rational" || cls.GetUnderlyingSystemType() != typeof(RubyRational)) {
                return null;
            }
            _rationalClass = cls;
            return cls;
        }

        /// <summary>
        /// rational.rb's Rational.__new_internal__ (MRI's nurat_s_new_internal, which a C function like
        /// Time#subsec calls without going through Kernel#Rational): a frozen Rational with the given parts,
        /// which must already be canonical - in lowest terms, the denominator positive. Returns null when
        /// the Rational class is not there to make one (rational.rb not loaded yet).
        /// </summary>
        internal static object CreateCanonicalRational(RubyContext/*!*/ context, BigInteger numerator, BigInteger denominator) {
            Debug.Assert(denominator.Sign > 0 && BigInteger.GreatestCommonDivisor(numerator, denominator).IsOne);
            RubyClass cls = GetRationalClass(context);
            if (cls == null) {
                return null;
            }
            return new RubyRational(cls, Protocols.Normalize(numerator), Protocols.Normalize(denominator));
        }

        #endregion

        #region binding, block_given?, local_variables, caller, callcc, 1.9: __callee__, __method__

        [RubyMethod("binding", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("binding", RubyMethodAttributes.PublicSingleton)]
        public static Binding/*!*/ GetLocalScope(RubyScope/*!*/ scope, object self) {
            var result = Binding.Create(scope, scope.SelfObject);

            // Binding#source_location is the line that called #binding
            string path;
            int line;
            if (scope.TryGetCurrentSourceLocation(out path, out line)) {
                result.SourcePath = path;
                result.SourceLine = line;
            }
            return result;
        }

        [RubyMethod("block_given?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("block_given?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("iterator?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("iterator?", RubyMethodAttributes.PublicSingleton)]
        public static bool HasBlock(RubyScope/*!*/ scope, object self) {
            var methodScope = scope.GetInnerMostMethodScope();
            return methodScope != null && methodScope.BlockParameter != null;
        }

        [RubyMethod("local_variables", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("local_variables", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetLocalVariableNames(RubyScope/*!*/ scope, object self) {
            return scope.GetLocalVariableSymbols();
        }

        [RubyMethod("caller", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("caller", RubyMethodAttributes.PublicSingleton)]
        [RubyStackTraceHidden]
        public static RubyArray GetStackTrace(RubyContext/*!*/ context, object self,
            [DefaultProtocol, DefaultParameterValue(1)]int skipFrames) {
            return GetStackTrace(context, skipFrames, -1);
        }

        [RubyMethod("caller", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("caller", RubyMethodAttributes.PublicSingleton)]
        [RubyStackTraceHidden]
        public static RubyArray GetStackTrace(RubyContext/*!*/ context, object self,
            [DefaultProtocol]int skipFrames, [DefaultProtocol]int length) {
            if (length < 0) {
                throw RubyExceptions.CreateArgumentError("negative size ({0})", length);
            }
            return GetStackTrace(context, skipFrames, length);
        }

        // caller(n, nil) is caller(n): an explicit nil means "no limit", not "no frames".
        [RubyMethod("caller", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("caller", RubyMethodAttributes.PublicSingleton)]
        [RubyStackTraceHidden]
        public static RubyArray GetStackTrace(RubyContext/*!*/ context, object self,
            [DefaultProtocol]int skipFrames, DynamicNull length) {
            return GetStackTrace(context, skipFrames, -1);
        }

        // caller(first..last) is caller(first, last - first + 1); an omitted end means
        // "to the bottom of the stack" and an omitted beginning means "from the top".
        [RubyMethod("caller", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("caller", RubyMethodAttributes.PublicSingleton)]
        [RubyStackTraceHidden]
        public static RubyArray GetStackTrace(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, object self,
            [NotNull]Range/*!*/ range) {

            int begin = (range.Begin != null) ? Protocols.CastToFixnum(fixnumCast, range.Begin) : 0;
            var frames = GetStackTrace(context, begin, -1);
            if (frames == null || range.End == null) {
                return frames;
            }

            // a negative end counts back from the bottom of the stack, as in Array#[]
            int end = Protocols.CastToFixnum(fixnumCast, range.End);
            if (end < 0) {
                end += begin + frames.Count;
            }

            int length = Math.Max(end - begin + (range.ExcludeEnd ? 0 : 1), 0);
            if (length < frames.Count) {
                frames.RemoveRange(length, frames.Count - length);
            }
            return frames;
        }

        /// <summary>
        /// The frames below <paramref name="skipFrames"/>, at most <paramref name="length"/> of
        /// them (-1 for all). MRI answers nil rather than an empty array when the starting frame
        /// is past the bottom of the stack, which is how a caller tells "no such frame" apart
        /// from "no frames left".
        /// </summary>
        private static RubyArray GetStackTrace(RubyContext/*!*/ context, int skipFrames, int length) {
            if (skipFrames < 0) {
                throw RubyExceptions.CreateArgumentError("negative level ({0})", skipFrames);
            }

            var frames = RubyExceptionData.CreateBacktrace(context, skipFrames);
            if (frames.Count == 0 && skipFrames > 0 && skipFrames > RubyExceptionData.CreateBacktrace(context, 0).Count) {
                return null;
            }

            if (length >= 0 && length < frames.Count) {
                frames.RemoveRange(length, frames.Count - length);
            }
            return frames;
        }

        //callcc

        /// <summary>
        /// The name the enclosing method was defined under, or nil outside any method. A block
        /// reports the method it was written in, which is why this walks the scope chain rather
        /// than only looking at the innermost scope.
        ///
        /// Without it every library method written as `block or return enum_for(__method__)` -
        /// which is how the standard library returns an enumerator - raised NoMethodError.
        /// </summary>
        [RubyMethod("__method__", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("__method__", RubyMethodAttributes.PublicSingleton)]
        public static object GetCurrentMethodName(RubyScope/*!*/ scope, object self) {
            string name = scope.GetCurrentMethodName();
            return name != null ? scope.RubyContext.EncodeIdentifier(name) : null;
        }

        /// <summary>
        /// Like __method__, but the name the method was called by, which differs through an alias.
        /// </summary>
        [RubyMethod("__callee__", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("__callee__", RubyMethodAttributes.PublicSingleton)]
        public static object GetCurrentCalleeName(RubyScope/*!*/ scope, object self) {
            string name = scope.GetCurrentMethodName(true);
            return name != null ? scope.RubyContext.EncodeIdentifier(name) : null;
        }

        #endregion

        #region throw, catch, loop, proc, lambda

        private sealed class ThrowCatchUnwinder : StackUnwinder {
            public readonly object Label;

            internal ThrowCatchUnwinder(object label, object returnValue)
                : base(returnValue) {
                Label = label;
            }
        }

        [ThreadStatic]
        private static Stack<object> _catchSymbols;

        // Since Ruby 1.9 the tag may be omitted, in which case catch invents one and yields it.
        [RubyMethod("catch", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("catch", RubyMethodAttributes.PublicSingleton)]
        public static object Catch(RubyContext/*!*/ context, BlockParam/*!*/ block, object self) {
            return Catch(block, self, new RubyObject(context.ObjectClass));
        }

        [RubyMethod("catch", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("catch", RubyMethodAttributes.PublicSingleton)]
        public static object Catch(BlockParam/*!*/ block, object self, object label) {
            if (block == null) {
                throw RubyExceptions.NoBlockGiven();
            }

            try {
                if (_catchSymbols == null) {
                    _catchSymbols = new Stack<object>();
                }
                _catchSymbols.Push(label);

                try {
                    object result;
                    block.Yield(label, out result);
                    return result;
                } catch (ThrowCatchUnwinder unwinder) {
                    if (ReferenceEquals(unwinder.Label, label)) {
                        return unwinder.ReturnValue;
                    }

                    throw;
                }
            } finally {
                _catchSymbols.Pop();
            }
        }

        [RubyMethod("throw", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("throw", RubyMethodAttributes.PublicSingleton)]
        public static void Throw(RubyContext/*!*/ context, object self, object label, [DefaultParameterValue(null)]object returnValue) {
            if (_catchSymbols == null || !_catchSymbols.Contains(label, ReferenceEqualityComparer<object>.Instance)) {
                // UncaughtThrowError (2.2), not NameError; it carries the tag and the value.
                throw new UncaughtThrowError(
                    String.Format("uncaught throw {0}", context.Inspect(label).ToAsciiString()), label, returnValue);
            }

            throw new ThrowCatchUnwinder(label, returnValue);
        }

        [RubyMethod("loop", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("loop", RubyMethodAttributes.PublicSingleton)]
        public static object Loop(BlockParam/*!*/ block, object self) {
            if (block == null) {
                throw RubyExceptions.NoBlockGiven();
            }

            while (true) {
                object result;
                if (block.Yield(out result)) {
                    return result;
                }
            }
        }

        [RubyMethod("lambda", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("lambda", RubyMethodAttributes.PublicSingleton)]
        public static Proc/*!*/ CreateLambda(BlockParam/*!*/ block, object self) {
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }

            // lambda(&a_lambda) is that lambda
            if (block.Proc.Kind == ProcKind.Lambda) {
                return block.Proc;
            }

            // Since 3.3 a Proc object is not turned into a lambda: lambda(&a_proc) is an error.
            if (!block.IsLiteralBlock) {
                throw RubyExceptions.CreateArgumentError("the lambda method requires a literal block");
            }

            return block.Proc.ToLambda(null);
        }

        [RubyMethod("proc", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("proc", RubyMethodAttributes.PublicSingleton)]
        public static Proc/*!*/ CreateProc(BlockParam/*!*/ block, object self) {
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }

            return block.Proc;
        }

        #endregion

        #region raise, fail

        [RubyMethod("raise", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("raise", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("fail", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("fail", RubyMethodAttributes.PublicSingleton)]
        [RubyStackTraceHidden]
        [ReceivesKeywords]
        public static void RaiseException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyScope/*!*/ scope, object self, params object[]/*!*/ args) {

            RubyContext context = scope.RubyContext;
            Exception exception = CreateExceptionToRaise(respondToStorage, storage0, storage1, setBackTraceStorage, context, args);
            if ((TracePoint.ActiveEvents & (int)TraceEvents.Raise) != 0) {
                TracePoint.OnException(TraceEvents.Raise, scope, context, exception);
            }

            // An exception raised before whose backtrace was since set to nil gets a new one, as in
            // MRI. The interpreter keeps the frames of the first throw on the exception and would
            // hand those back again.
            if (!RubyExceptionData.GetInstance(exception).HasBacktrace) {
                exception.RemoveData(typeof(Microsoft.Scripting.Interpreter.InterpretedFrameInfo));
                RubyExceptionData.GetInstance(exception).ResetRaiseBacktrace();
            }

            // rethrow semantics, preserves the backtrace associated with the exception:
            throw exception;
        }

        /// <summary>
        /// Where a source file that has run was when it started - see
        /// RubyContext.RegisterSourceFileLocation - or nil. Private, and not part of MRI's Kernel.
        /// </summary>
        [RubyMethod("__source_location_of__", RubyMethodAttributes.PrivateInstance)]
        public static MutableString GetSourceFileLocation(RubyContext/*!*/ context, object self, [NotNull]MutableString/*!*/ path) {
            string result = context.TryGetSourceFileLocation(context.DecodePath(path));
            return (result != null) ? context.EncodePath(result) : null;
        }

        /// <summary>
        /// Builds the exception `raise` would throw, without throwing it. Fiber#raise is written
        /// in Ruby (Src/StdLib/ironruby/ruby4.rb) and has to hand the exception to another fiber,
        /// so it needs the argument handling - including `cause:`, which MRI resolves in the
        /// *calling* context - without the throw. Private, and not part of MRI's Kernel.
        /// </summary>
        [RubyMethod("__build_exception__", RubyMethodAttributes.PrivateInstance)]
        [ReceivesKeywords]
        public static Exception/*!*/ BuildException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, object self, params object[]/*!*/ args) {

            return CreateExceptionToRaise(respondToStorage, storage0, storage1, setBackTraceStorage, context, args);
        }

        /// <summary>
        /// The whole of `raise [exception [, message [, backtrace]]] [, cause: c]`, shared by
        /// Kernel#raise, Thread#raise and (via __build_exception__) Fiber#raise so that all
        /// three agree.
        /// </summary>
        internal static Exception/*!*/ CreateExceptionToRaise(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, object[]/*!*/ args) {

            return CreateExceptionToRaise(respondToStorage, storage0, storage1, setBackTraceStorage, context, args, true);
        }

        internal static Exception/*!*/ CreateExceptionToRaise(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, object[]/*!*/ args, bool bareRaiseReRaisesCurrentException) {

            object cause;
            bool hasCause = TryTakeCauseKeyword(ref args, out cause);

            if (args.Length > 3) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..3)", args.Length);
            }

            if (args.Length == 0 && hasCause) {
                throw RubyExceptions.CreateArgumentError("only cause is given with no arguments");
            }

            Exception exception;
            if (args.Length == 0) {
                // bare `raise` re-raises $!, or a fresh RuntimeError with an empty message.
                exception = bareRaiseReRaisesCurrentException ? context.CurrentException : null;
                if (exception == null) {
                    exception = RubyExceptionData.InitializeException(new RuntimeError(""), MutableString.CreateEmpty());
                }
            } else {
                exception = MakeException(respondToStorage, storage0, storage1, args);

                if (args.Length >= 3 && args[2] != null) {
                    if (!(args[2] is MutableString || args[2] is RubyArray)) {
                        throw RubyExceptions.CreateTypeError("backtrace must be an Array of String or an Array of Thread::Backtrace::Location");
                    }
                    var site = setBackTraceStorage.GetCallSite("set_backtrace", 1);
                    site.Target(site, exception, args[2]);
                }
            }

            SetCause(context, exception, hasCause, cause);

            // An exception with no backtrace - new, or cleared by set_backtrace(nil) - gets one from
            // where it is raised now. The interpreter keeps the frames of the first throw on the
            // exception and would otherwise report those again.
            if (!RubyExceptionData.GetInstance(exception).HasBacktrace) {
                exception.SetData(typeof(Microsoft.Scripting.Interpreter.InterpretedFrameInfo), null);
                RubyExceptionData.GetInstance(exception).ResetRaiseBacktrace();
            }
            return exception;
        }

        private static Exception/*!*/ MakeException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            object[]/*!*/ args) {

            object obj = args[0];

            // `raise "boom"` is RuntimeError. A String is only accepted on its own: MRI answers
            // "exception class/object expected" for `raise "boom", "more"`, because there the
            // first argument has to be something that responds to #exception.
            var message = obj as MutableString;
            if (message != null && args.Length == 1) {
                // The CLR message is for display only - the Ruby-visible message is the MutableString
                // handed to InitializeException - so it must not reject a message that holds bytes
                // invalid in its encoding. MRI raises such a message happily.
                return RubyExceptionData.InitializeException(new RuntimeError(message.ToClrString()), message);
            }

            // the symbol comes from the context's identifier cache: interning it anew was most of
            // what this check cost
            var respondTo = respondToStorage.GetCallSite();
            if (!RubyOps.IsTrue(respondTo.Target(respondTo, obj, respondToStorage.Context.StringifyIdentifier("exception")))) {
                throw RubyExceptions.CreateTypeError("exception class/object expected");
            }

            object result;
            if (args.Length >= 2) {
                var site = storage1.GetCallSite("exception");
                result = site.Target(site, obj, args[1]);
            } else {
                var site = storage0.GetCallSite("exception");
                result = site.Target(site, obj);
            }

            var exception = result as Exception;
            if (exception == null) {
                // MRI distinguishes "you passed something that isn't raisable at all" from
                // "#exception answered something that isn't an exception".
                throw RubyExceptions.CreateTypeError("exception object expected");
            }
            return exception;
        }

        /// <summary>
        /// Splits a trailing `cause:` keyword off the argument list.
        ///
        /// IronRuby has no real keyword arguments - they arrive as a trailing Hash - so this can
        /// only go by shape: a trailing Hash that was written as keywords and whose single key
        /// is :cause. That is deliberate, because MRI passes any *other* trailing hash on to the
        /// exception constructor (`raise MyError, data: 42`); and an explicitly braced
        /// `raise "msg", {cause: e}` is positional, which is why the keyword flag is checked.
        /// </summary>
        private static bool TryTakeCauseKeyword(ref object[]/*!*/ args, out object cause) {
            cause = null;
            if (args.Length == 0) {
                return false;
            }

            var hash = args[args.Length - 1] as Hash;
            if (hash == null || hash.Count != 1 || !hash.IsKeywordArguments) {
                return false;
            }

            foreach (var entry in hash) {
                var key = entry.Key as RubySymbol;
                if (key == null || key.ToString() != "cause") {
                    return false;
                }
                cause = entry.Value;
            }

            var rest = new object[args.Length - 1];
            Array.Copy(args, rest, rest.Length);
            args = rest;
            return true;
        }

        private static void SetCause(RubyContext/*!*/ context, Exception/*!*/ exception, bool hasCause, object cause) {
            Exception causeException;
            if (hasCause) {
                if (cause == null) {
                    // `cause: nil` says "do not attach whatever is being handled", not "forget the
                    // cause you have": an exception that already has one keeps it.
                    if (RubyExceptionData.GetInstance(exception).HasCause) {
                        return;
                    }
                    causeException = null;
                } else {
                    causeException = cause as Exception;
                    if (causeException == null) {
                        throw RubyExceptions.CreateTypeError("exception object expected");
                    }
                }
            } else {
                // An exception that already has a cause keeps it: re-raising a rescued exception
                // keeps the chain it was first raised with, and does not pick up the exception
                // currently being handled - which is very often the one it caused, and would then
                // look circular. MRI leaves it alone without even looking.
                if (RubyExceptionData.GetInstance(exception).HasCause) {
                    return;
                }

                // no explicit cause: chain to whatever is currently being handled ($!)
                causeException = context.CurrentException;
            }

            // `raise e, cause: e` is not circular, it just leaves the cause unset.
            if (causeException != exception) {
                for (Exception c = causeException; c != null; c = RubyExceptionData.GetInstance(c).Cause) {
                    if (c == exception) {
                        throw RubyExceptions.CreateArgumentError("circular causes");
                    }
                }
            }

            // A named cause replaces whatever was there; an implicit one only fills in a blank,
            // and by here there is one to fill.
            RubyExceptionData.GetInstance(exception).SetCause(causeException);
        }

        #endregion


        #region =~, !~, ===, <=>, eql?, hash, to_s, inspect, to_a

        // Object#=~ is gone since Ruby 3.2; #!~ calls whatever #=~ the receiver has, and a
        // NoMethodError if it has none.
        // The scope is handed to #=~ rather than left behind: $~ belongs to the frame that wrote
        // the `!~`, and Regexp#=~ sets it on the scope it is given. Without this, `if RE !~ line`
        // matched and then left $1 as whatever it had been - httpclient parses every status line
        // that way.
        [RubyMethod("!~")]
        public static bool NotMatch(CallSiteStorage<Func<CallSite, RubyScope, object, object, object>>/*!*/ match,
            RubyScope/*!*/ scope, object self, object other) {
            var site = match.GetCallSite("=~", new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf));
            return RubyOps.IsFalse(site.Target(site, scope, self, other));
        }

        // calls == by default
        [RubyMethod("===")]
        public static bool CaseEquals(BinaryOpStorage/*!*/ equals, object self, object other) {
            return Protocols.IsEqual(equals, self, other);
        }

        // calls == by default
        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ equals, object self, object other) {
            return Protocols.IsEqual(equals, self, other) ? ScriptingRuntimeHelpers.Int32ToObject(0) : null;
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("eql?")]
        public static bool ValueEquals([NotNull]IRubyObject/*!*/ self, object other) {
            return self.BaseEquals(other);
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("eql?")]
        public static bool ValueEquals(object self, object other) {
            return Object.Equals(self, other);
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("hash")]
        public static int Hash([NotNull]IRubyObject/*!*/ self) {
            return self.BaseGetHashCode();
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("hash")]
        public static int Hash(object self) {
            return self == null ? RubyUtils.NilObjectId : self.GetHashCode();
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS([NotNull]IRubyObject/*!*/ self) {
            return RubyUtils.ObjectBaseToMutableString(self);
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(object self) {
            return self == null ? MutableString.CreateEmpty() : MutableString.Create(self.ToString(), RubyEncoding.UTF8);
        }

        /// <summary>
        /// Returns a string containing a human-readable representation of obj.
        /// If not overridden, uses the to_s method to generate the string. 
        /// </summary>
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(UnaryOpStorage/*!*/ inspectStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            object self) {

            RubyClass cls;
            var context = tosConversion.Context;
            // Ruby 1.8 fell back to #to_s when the object had no instance variables; 1.9 dropped
            // that, so a stateless object with a custom #to_s still inspects as "#<Foo:0x...>",
            // and so does a BasicObject, which has no #to_s to fall back to at all.
            // BasicObject is a library class rather than one defined in Ruby, so it needs naming
            // alongside Object: it is the one class whose instances have no #to_s to fall back to.
            if ((cls = context.GetClassOf(self)).IsRubyClass || cls.IsObjectClass || cls == context.BasicObjectClass) {
                return RubyUtils.InspectObject(inspectStorage, tosConversion, self);
            } else {
                var site = tosConversion.GetSite(ConvertToSAction.Make(context));
                return site.Target(site, self);
            }
        }

        #endregion

        #region nil?, __id__, object_id

        // thread-safe:
        [RubyMethod("nil?")]
        public static bool IsNil(object self) {
            return self == null;
        }

        // There is no Kernel#id.  Ruby 1.8 had one, deprecated in favour of #object_id,
        // and 1.9 removed it - so `obj.respond_to?(:id)` has to answer false.  Keeping it
        // was not merely a stale warning: ActiveRecord's predicate builder asks exactly
        // that question to decide whether a value is a record to take the primary key of,
        // and every object answering yes built the wrong SQL.

        // #__id__ is defined on BasicObject, where MRI keeps it, not here.
        [RubyMethod("object_id")]
        public static object GetObjectId(RubyContext/*!*/ context, object self) {
            return ClrInteger.Narrow(RubyUtils.GetObjectId(context, self));
        }

        #endregion

        #region clone, dup

        /// <summary>
        /// Kernel#clone(freeze: nil) (kernel.rb in MRI). It was a prelude method with the keyword, in front of
        /// this one: a Ruby keyword-parameter entry on every #clone, ~2x the cost of #dup. A freeze: of nil is
        /// a clone without the option; true or false is handed on to #initialize_clone (__ir_clone_with_freeze__).
        /// </summary>
        [RubyMethod("clone")]
        public static object/*!*/ Clone(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ initializeCloneStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            object self, [Keywords, Optional]object options) {

            if (options != Missing.Value) {
                var keywords = options as Hash;
                if (keywords == null || !keywords.IsKeywordArguments) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 1, expected 0)");
                }
                var context = allocateStorage.Context;
                object freeze = null;
                List<string> unknown = null;
                foreach (var entry in keywords) {
                    var key = entry.Key as RubySymbol;
                    if (key == null || key.ToString() != "freeze") {
                        (unknown ??= new List<string>()).Add(context.Inspect(entry.Key).ToString());
                    } else {
                        freeze = entry.Value;
                    }
                }
                if (unknown != null) {
                    throw RubyExceptions.CreateArgumentError("unknown keyword{0}: {1}", unknown.Count > 1 ? "s" : "", String.Join(", ", unknown));
                }
                if (freeze != null) {
                    if (!(freeze is bool)) {
                        throw RubyExceptions.CreateArgumentError("unexpected value for freeze: {0}",
                            RubyExceptions.MessageTypeName(context.GetClassName(freeze)));
                    }
                    return CloneWithFreeze(initializeCloneStorage, allocateStorage, self, (bool)freeze);
                }
            }
            return Clone(initializeCopyStorage, allocateStorage, true, self);
        }

        /// <summary>
        /// Object identity for the prelude, BasicObject#equal?'s, which an object's own #equal? cannot change:
        /// Kernel#puts asks it whether it is running on $stdout itself, and a writer whose #equal? said no sent
        /// it round for ever.
        /// </summary>
        [RubyMethod("__ir_equal__", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSameObject(object self, object other) {
            return BasicObjectOps.IsEqual(self, other);
        }

        /// <summary>
        /// MRI's rb_inspect for the prelude: #inspect, escaped when its result is in an
        /// encoding the output cannot take (see RubyContext.Inspect).
        /// </summary>
        [RubyMethod("__ir_inspect__", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ InspectForDisplay(RubyContext/*!*/ context, object self, object value) {
            return context.Inspect(value);
        }

        /// <summary>
        /// ARGF#gets and #readline (argf.rb) are aliases of these, so that the line read lands in
        /// the $_ of the code that called them, as MRI's C implementation does; the reading
        /// itself is the prelude's __ir_gets__ / __ir_readline__.
        /// </summary>
        [RubyMethod("__ir_argf_gets__", RubyMethodAttributes.PrivateInstance)]
        [ReceivesKeywords]
        public static object ArgfGets(CallSiteStorage<Func<CallSite, object, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, params object[]/*!*/ args) {
            return ArgfRead(storage, scope, self, "__ir_gets__", args);
        }

        [RubyMethod("__ir_argf_readline__", RubyMethodAttributes.PrivateInstance)]
        [ReceivesKeywords]
        public static object ArgfReadline(CallSiteStorage<Func<CallSite, object, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, params object[]/*!*/ args) {
            return ArgfRead(storage, scope, self, "__ir_readline__", args);
        }

        private static object ArgfRead(CallSiteStorage<Func<CallSite, object, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, string/*!*/ method, object[]/*!*/ args) {
            var site = storage.GetCallSite(method, new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasSplattedArgument));
            object line = site.Target(site, self, RubyOps.MakeArrayN(args));
            scope.GetInnerMostClosureScope().LastInputLine = line;
            return line;
        }

        /// <summary>
        /// pattern === value, with a Regexp's match landing in the $~ of <paramref name="target"/>:
        /// a Proc (the block of grep) or a Binding (the frame that called grep).
        /// </summary>
        [RubyMethod("__ir_case_match__", RubyMethodAttributes.PrivateInstance)]
        public static bool CaseMatchInto(ConversionStorage<MutableString>/*!*/ stringTryCast, BinaryOpStorage/*!*/ caseEquals,
            object self, object pattern, object value, object target) {

            var regex = pattern as RubyRegex;
            RubyScope targetScope = (target is Proc) ? ((Proc)target).LocalScope : (target is Binding) ? ((Binding)target).LocalScope : null;
            if (regex != null && targetScope != null) {
                return RegexpOps.CaseCompare(stringTryCast, targetScope, regex, value);
            }
            var site = caseEquals.GetCallSite("===");
            return RubyOps.IsTrue(site.Target(site, pattern, value));
        }

        /// <summary>
        /// Enumerator::Lazy#grep and #grep_v (ruby4.rb aliases them to these). Without a block the
        /// matches of a Regexp land in the $~ of the code that called grep - which is where the
        /// block that later takes the values (each, map, ...) reads it - and only a library method
        /// is given that frame; the work is __ir_lazy_grep_impl__'s.
        /// </summary>
        [RubyMethod("__ir_lazy_grep__", RubyMethodAttributes.PrivateInstance)]
        public static object LazyGrep(CallSiteStorage<Func<CallSite, object, object, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, object pattern) {
            return LazyGrep(storage, scope, block, self, pattern, false);
        }

        [RubyMethod("__ir_lazy_grep_v__", RubyMethodAttributes.PrivateInstance)]
        public static object LazyGrepV(CallSiteStorage<Func<CallSite, object, object, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, object pattern) {
            return LazyGrep(storage, scope, block, self, pattern, true);
        }

        private static object LazyGrep(CallSiteStorage<Func<CallSite, object, object, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, object pattern, bool invert) {
            var site = storage.GetCallSite("__ir_lazy_grep_impl__", new RubyCallSignature(4, RubyCallFlags.HasImplicitSelf));
            return site.Target(site, self, pattern, ScriptingRuntimeHelpers.BooleanToObject(invert),
                new Binding(scope), (block != null) ? block.Proc : null);
        }

        /// <summary>
        /// The prelude's "does obj answer this message" (the conversions in rational.rb and complex.rb ask it of
        /// whatever they are handed - a BasicObject included, which has no #respond_to?): true if obj has the method
        /// in any visibility, otherwise what its #respond_to? says if it has a public or protected one. An object
        /// that can have no singleton class (an Integer, a Float, a Symbol, a frozen String) is asked #respond_to?.
        /// The lookup is the one `class &lt;&lt; obj; self; end.method_defined?' made, but on the class obj has - its
        /// singleton class only if it already has one: making one to ask through gave every value converted (each
        /// Rational of every Time#subsec) a class of its own, which each call site it reached then had to bind afresh.
        /// </summary>
        [RubyMethod("__ir_responds_to__", RubyMethodAttributes.PrivateInstance)]
        public static bool RespondsTo(ConversionStorage<string>/*!*/ stringCast, RespondToStorage/*!*/ respondToStorage,
            object self, object obj, object methodName) {

            var context = respondToStorage.Context;
            var respondToSite = respondToStorage.GetCallSite();
            if (!RubyUtils.CanDefineSingletonMethod(obj)) {
                return Protocols.IsTrue(respondToSite.Target(respondToSite, obj, methodName));
            }

            // method_defined? || private_method_defined?, as Module#method_defined? answers them:
            string name = Protocols.CastToSymbol(stringCast, methodName);
            RubyClass cls = context.GetImmediateClassOf(obj);
            if (GetDefinedVisibility(cls, name) != RubyMethodVisibility.None) {
                return true;
            }

            var respondTo = GetDefinedVisibility(cls, "respond_to?");
            return (respondTo == RubyMethodVisibility.Public || respondTo == RubyMethodVisibility.Protected) &&
                Protocols.IsTrue(respondToSite.Target(respondToSite, obj, methodName));
        }

        private static RubyMethodVisibility GetDefinedVisibility(RubyClass/*!*/ cls, string/*!*/ name) {
            var method = cls.GetMethodLookup(name);
            if (!method.Found) {
                return RubyMethodVisibility.None;
            }
            var libraryMethod = method.Info as RubyLibraryMethodInfo;
            return (libraryMethod != null && libraryMethod.IsNotImplemented) ? RubyMethodVisibility.None : method.Visibility;
        }

        [RubyMethod("__ir_clone_with_freeze__", RubyMethodAttributes.PrivateInstance)]
        public static object/*!*/ CloneWithFreeze(
            CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            object self, bool freeze) {

            var context = allocateStorage.Context;

            object result;
            if (!RubyUtils.TryDuplicateObject(initializeCopyStorage, allocateStorage, self, freeze, out result)) {
                return self;
            }
            return result;
        }

        [RubyMethod("dup")]
        public static object/*!*/ Duplicate(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            object self) {

            return Clone(initializeCopyStorage, allocateStorage, false, self);
        }

        private static object/*!*/ Clone(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            bool isClone, object self) {

            var context = allocateStorage.Context;

            object result;
            if (!RubyUtils.TryDuplicateObject(initializeCopyStorage, allocateStorage, self, isClone, out result)) {
                // Ruby 2.4 stopped raising here: #dup and #clone of one of MRI's "special objects"
                // (nil, true, false, Integer, Float, Symbol - and Rational/Complex, which handle
                // themselves in ruby4.rb) answer the receiver rather than TypeError("can't dup NilClass").
                return self;
            }
            return result;
        }

        #endregion

        // Object#type was Ruby 1.8's spelling of Object#class and was removed in 1.9, so it
        // is gone here too.  It was not harmless in either direction:
        //   - `obj.respond_to?(:type)` is a live test in real code.  HexaPDF asks it to tell
        //     already-segmented text items from raw ones (TextLayouter#fit); with a Kernel#type
        //     in the way it skipped segmentation and laid out nothing - an empty PDF, no error.
        //   - it shadowed a real method of the same name: ActiveRecord's TimeZoneConverter#type
        //     is a DelegateClass method, so a method_missing away, and Object#type answered the
        //     class instead - every :time column cast wrongly, and railsbench's seeded rows
        //     never saved.
        #region class, extend, instance_of?, is_a?, kind_of? (thread-safe)

        [RubyMethod("class")]
        public static RubyClass/*!*/ GetClass(RubyContext/*!*/ context, object self) {
            return context.GetClassOf(self);
        }

        // thread-safe:
        [RubyMethod("is_a?")]
        [RubyMethod("kind_of?")]
        public static bool IsKindOf(object self, RubyModule/*!*/ other) {
            ContractUtils.RequiresNotNull(other, "other");
            return other.Context.IsKindOf(self, other);
        }

        // thread-safe:
        [RubyMethod("instance_of?")]
        public static bool IsOfClass(object self, RubyModule/*!*/ other) {
            ContractUtils.RequiresNotNull(other, "other");
            return other.Context.GetClassOf(self) == other;
        }

        [RubyMethod("extend")]
        public static object Extend(
            CallSiteStorage<Func<CallSite, RubyModule, object, object>>/*!*/ extendObjectStorage,
            CallSiteStorage<Func<CallSite, RubyModule, object, object>>/*!*/ extendedStorage,
            object self, [NotNull]RubyModule/*!*/ module, [NotNullItems]params RubyModule/*!*/[]/*!*/ modules) {

            Assert.NotNull(modules);

            // a class is a Module, but not one that can be mixed in
            foreach (var mixin in new[] { module }.Concat(modules)) {
                if (mixin is RubyClass) {
                    throw RubyExceptions.CreateTypeError("wrong argument type Class (expected Module)");
                }
                if (mixin.IsRefinement) {
                    throw RubyExceptions.CreateTypeError("Cannot extend object with refinement");
                }
            }

            // TODO: this is strange:
            RubyUtils.RequireMixins(module.GetOrCreateSingletonClass(), modules);

            var extendObject = extendObjectStorage.GetCallSite("extend_object", 1);
            var extended = extendedStorage.GetCallSite("extended", 1);

            // Kernel#extend_object inserts the module at the beginning of the object's singleton ancestors list;
            // ancestors after extend: [modules[0], modules[1], ..., modules[N-1], self-singleton, ...]
            for (int i = modules.Length - 1; i >= 0; i--) {
                extendObject.Target(extendObject, modules[i], self);
                extended.Target(extended, modules[i], self);
            }

            extendObject.Target(extendObject, module, self);
            extended.Target(extended, module, self);

            return self;
        }

        #endregion

        #region frozen?, freeze

        [RubyMethod("frozen?")]
        public static bool Frozen([NotNull]MutableString/*!*/ self) {
            return self.IsFrozen;
        }

        [RubyMethod("frozen?")]
        public static bool Frozen(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self) || self is double || self is float) {
                // Immediate values - Integer, Float, Symbol, nil, true, false - cannot hold
                // state, and Ruby reports exactly that by calling them frozen. Answering
                // false said the opposite of the truth: nothing can modify them at all.
                return true;
            }
            return context.IsObjectFrozen(self);
        }

        [RubyMethod("freeze")]
        public static object Freeze(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return self; // can't freeze value types
            }
            context.FreezeObject(self);
            return self;
        }

        #endregion

        #region eval

        [RubyMethod("eval", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("eval", RubyMethodAttributes.PublicSingleton)]
        public static object Evaluate(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]MutableString/*!*/ code,
            [Optional]Binding binding, [Optional, NotNull]MutableString file, [DefaultParameterValue(1)]int line) {

            RubyScope targetScope;
            object targetSelf;
            if (binding != null) {
                targetScope = binding.LocalScope;
                targetSelf = binding.SelfObject;
            } else {
                // The locals a string defines are its own: a second eval does not see them.
                targetScope = new RubyBindingCopyScope(scope, self);
                targetSelf = self;
            }
            return RubyUtils.Evaluate(code, targetScope, targetSelf, null, file, line);
        }

        /// <summary>
        /// A Proc used to be accepted in place of a Binding; Ruby stopped accepting one in 1.9 and
        /// reports it as the wrong type rather than evaluating in the block's scope.
        /// </summary>
        [RubyMethod("eval", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("eval", RubyMethodAttributes.PublicSingleton)]
        public static object Evaluate(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]MutableString/*!*/ code,
            [NotNull]Proc/*!*/ procBinding, [Optional, NotNull]MutableString file, [DefaultParameterValue(1)]int line) {

            throw RubyExceptions.CreateTypeError("wrong argument type proc (expected binding)");
        }

        #endregion

        #region instance_variables, instance_variable_defined?, instance_variable_get, instance_variable_set, remove_instance_variable

        [RubyMethod("instance_variables")]
        public static RubyArray/*!*/ GetInstanceVariableNames(RubyContext/*!*/ context, object self) {
            return context.StringifyIdentifiers(context.GetInstanceVariableNames(self));
        }

        /// <summary>
        /// MRI takes a Symbol or a String here and raises TypeError for anything else; the
        /// [DefaultProtocol]string binding used to put an Integer through the legacy
        /// "Fixnum as Symbol" path and produce a NameError about an unrelated name.
        /// </summary>
        private static string/*!*/ ToVariableName(ConversionStorage<string>/*!*/ stringCast, object name) {
            // what the conversion site does for these two (ConvertToSymbolAction.TryConvertStringOrSymbol), without the site
            if (name is RubySymbol symbol) {
                return symbol.ToString();
            }
            if (name is MutableString str) {
                return str.ConvertToString();
            }
            return Protocols.CastToSymbol(stringCast, name);
        }

        /// <summary>Internal overload for callers that already have the name as a string.</summary>
        public static object InstanceVariableGet(RubyContext/*!*/ context, object self, string/*!*/ name) {
            object value;
            if (!context.TryGetInstanceVariable(self, name, out value)) {
                RubyUtils.CheckInstanceVariableName(context, self, name);
                return null;
            }
            return value;
        }

        [RubyMethod("instance_variable_get")]
        public static object InstanceVariableGet(ConversionStorage<string>/*!*/ stringCast,
            RubyContext/*!*/ context, object self, object nameArg) {
            string name = ToVariableName(stringCast, nameArg);
            object value;
            if (!context.TryGetInstanceVariable(self, name, out value)) {
                // We didn't find it, check if the name is valid
                RubyUtils.CheckInstanceVariableName(context, self, name, nameArg);
                value = null;
            }
            return context.RactorsStarted ? RactorIsolation.CheckInstanceVariableRead(context, self, name, value) : value;
        }

        [RubyMethod("instance_variable_set")]
        public static object InstanceVariableSet(ConversionStorage<string>/*!*/ stringCast,
            RubyContext/*!*/ context, object self, object nameArg, object value) {
            string name = ToVariableName(stringCast, nameArg);
            RubyUtils.CheckInstanceVariableName(context, self, name, nameArg);
            if (context.RactorsStarted) {
                RactorIsolation.CheckInstanceVariableWrite(context, self);
            }
            context.SetInstanceVariable(self, name, value);
            return value;
        }

        /// <summary>
        /// Where Data (ruby4.rb) keeps its members' values, and a Data class its member list: in
        /// instance variables whose names Ruby cannot spell, so that #instance_variables and
        /// Marshal's instance variable list do not show them - CRuby keeps them outside the ivar
        /// table - while dup, clone and the shape-based storage treat them like any other.
        /// </summary>
        [RubyMethod("__ir_hidden_ivar__", RubyMethodAttributes.PrivateInstance)]
        public static object GetHiddenInstanceVariable(RubyContext/*!*/ context, object self, [NotNull]RubySymbol/*!*/ name) {
            object value;
            context.TryGetInstanceVariable(self, RubyUtils.GetHiddenInstanceVariableName(name.ToString()), out value);
            return value;
        }

        [RubyMethod("__ir_set_hidden_ivar__", RubyMethodAttributes.PrivateInstance)]
        public static object SetHiddenInstanceVariable(RubyContext/*!*/ context, object self, [NotNull]RubySymbol/*!*/ name, object value) {
            context.SetInstanceVariable(self, RubyUtils.GetHiddenInstanceVariableName(name.ToString()), value);
            return value;
        }

        [RubyMethod("instance_variable_defined?")]
        public static bool InstanceVariableDefined(ConversionStorage<string>/*!*/ stringCast,
            RubyContext/*!*/ context, object self, object nameArg) {
            string name = ToVariableName(stringCast, nameArg);
            object value;
            if (!context.TryGetInstanceVariable(self, name, out value)) {
                // We didn't find it, check if the name is valid
                RubyUtils.CheckInstanceVariableName(context, self, name, nameArg);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Public since 1.9, and the two ways it can fail are ordered: a name that is not an
        /// instance variable name at all is a NameError whatever the object, a frozen object
        /// refuses before it is asked whether it has the variable, and only then does a variable
        /// that is not there become a NameError.
        /// </summary>
        [RubyMethod("remove_instance_variable")]
        public static object RemoveInstanceVariable(RubyContext/*!*/ context, object/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name) {
            RubyUtils.CheckInstanceVariableName(context, self, name);
            if (context.RactorsStarted) {
                RactorIsolation.CheckInstanceVariableWrite(context, self);
            }

            object value;
            if (!context.TryRemoveInstanceVariable(self, name, out value)) {
                throw RubyExceptions.CreateNameError("instance variable {0} not defined", name);
            }

            return value;
        }

        #endregion

        #region global_variables

        [RubyMethod("global_variables", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("global_variables", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetGlobalVariableNames(RubyContext/*!*/ context, object self) {
            RactorIsolation.CheckGlobalVariables(context);
            RubyArray result = new RubyArray();
            lock (context.GlobalVariablesLock) {
                foreach (KeyValuePair<string, GlobalVariable> global in context.GlobalVariables) {
                    if (global.Value.IsEnumerated) {
                        // The table keys the variables without the sigil, because that is what the
                        // compiler passes around, but the names are `$stdin`, `$-I`, `$~`.
                        result.Add(context.StringifyIdentifier("$" + global.Key));
                    }
                }
            }
            return result;
        }

        #endregion

        #region trace_var, untrace_var

        /// <summary>
        /// The variable name as the global variable table keys it - `$` stripped, since that is
        /// what the compiler passes to RubyContext.SetGlobalVariable - and checked for existence,
        /// which is what MRI reports on before it looks at the handler.
        /// </summary>
        private static string/*!*/ GlobalVariableName(RubyContext/*!*/ context, string/*!*/ name, bool mustExist) {
            string key = name.StartsWith("$", StringComparison.Ordinal) ? name.Substring(1) : name;
            object value;
            if (mustExist && !context.TryGetGlobalVariable(null, key, out value)) {
                throw RubyExceptions.CreateNameError(String.Format("undefined global variable ${0}", key));
            }
            return key;
        }

        [RubyMethod("trace_var", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("trace_var", RubyMethodAttributes.PublicSingleton)]
        public static object TraceVariable(RubyScope/*!*/ scope, BlockParam block, object self,
            [DefaultProtocol, NotNull]string/*!*/ name) {

            return TraceVariable(scope, block, self, name, null);
        }

        [RubyMethod("trace_var", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("trace_var", RubyMethodAttributes.PublicSingleton)]
        public static object TraceVariable(RubyScope/*!*/ scope, BlockParam block, object self,
            [DefaultProtocol, NotNull]string/*!*/ name, object command) {

            object handler;
            var code = command as MutableString;
            if (code != null) {
                // A string is evaluated when the variable is assigned, in the context that
                // registered it - so it is the scope and self of this call that are kept.
                handler = new RubyContext.GlobalVariableTraceCommand(code, scope, self);
            } else if (command != null) {
                handler = command;
            } else if (block != null) {
                handler = block.Proc;
            } else {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }

            scope.RubyContext.AddGlobalVariableTrace(GlobalVariableName(scope.RubyContext, name, false), handler);
            return null;
        }

        [RubyMethod("untrace_var", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("untrace_var", RubyMethodAttributes.PublicSingleton)]
        public static object UntraceVariable(RubyContext/*!*/ context, object self,
            [DefaultProtocol, NotNull]string/*!*/ name) {

            return UntraceVariable(context, self, name, null);
        }

        [RubyMethod("untrace_var", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("untrace_var", RubyMethodAttributes.PublicSingleton)]
        public static object UntraceVariable(RubyContext/*!*/ context, object self,
            [DefaultProtocol, NotNull]string/*!*/ name, object command) {

            var removed = context.RemoveGlobalVariableTraces(GlobalVariableName(context, name, true), command);
            if (removed == null) {
                return null;
            }

            var result = new RubyArray(removed.Count);
            foreach (var handler in removed) {
                var traceCommand = handler as RubyContext.GlobalVariableTraceCommand;
                result.Add(traceCommand != null ? traceCommand.Code : handler);
            }
            return result;
        }

        #endregion

        #region autoload, autoloaded?

        // MRI puts the autoload on the cref class, which is where a `def` would go: inside a method
        // defined in a Class.new block that is the new class, not the lexically enclosing Object.
        // A singleton class stands for its real class (rb_class_real), so instance_eval autoloads on Object.
        private static RubyModule/*!*/ GetAutoloadOwner(RubyScope/*!*/ scope) {
            RubyModule owner = scope.GetMethodDefinitionOwner();
            return owner.IsSingletonClass ? ((RubyClass)owner).GetNonSingletonClass() : owner;
        }

        [RubyMethod("autoload", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("autoload", RubyMethodAttributes.PublicSingleton)]
        public static void SetAutoloadedConstant(ConversionStorage<MutableString>/*!*/ toPath, RubyScope/*!*/ scope, object self,
            [DefaultProtocol, NotNull]string/*!*/ constantName, object path) {
            ModuleOps.SetAutoloadedConstant(toPath, scope, GetAutoloadOwner(scope), constantName, path);
        }

        [RubyMethod("autoload?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("autoload?", RubyMethodAttributes.PublicSingleton)]
        public static MutableString GetAutoloadedConstantPath(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ constantName) {
            return ModuleOps.GetAutoloadedConstantPath(GetAutoloadOwner(scope), constantName);
        }

        [RubyMethod("autoload?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("autoload?", RubyMethodAttributes.PublicSingleton)]
        public static MutableString GetAutoloadedConstantPath(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ constantName,
            bool inherit) {
            return ModuleOps.GetAutoloadedConstantPath(GetAutoloadOwner(scope), constantName, inherit);
        }

        #endregion

        #region respond_to?

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("respond_to?")]
        public static bool RespondTo(RespondToCacheStorage/*!*/ respondToMissingStorage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName, [Optional]bool includePrivate) {

            var context = scope.RubyContext;
            RubyClass cls = context.GetImmediateClassOf(self);

            // A refinement active where #respond_to? was called counts, as it does for a call. Without
            // one with the name - nearly always - the answer comes from the class's lock-free
            // method-lookup cache, and the site keeps it while the class's method version stays the same.
            int version = -1;
            if (!context.IsRefinedMethodName(methodName) || scope.GetActiveRefinements().IsEmpty) {
                bool answer;
                if (respondToMissingStorage.TryGetAnswer(cls, methodName, includePrivate, out answer)) {
                    return answer;
                }

                var lookup = cls.GetMethodLookup(methodName, out version);

                // Without include_private only *public* methods count; MRI has answered false for
                // protected ones since 2.0. The visibility is the one at the name's entry point, so
                // `public :m' re-exporting an inherited private m counts.
                if (lookup.Found && (includePrivate || lookup.Visibility == RubyMethodVisibility.Public)) {
                    answer = IsImplemented(lookup.Info);
                    respondToMissingStorage.SetAnswer(cls, version, methodName, includePrivate, answer);
                    return answer;
                }
            } else {
                var visibility = includePrivate
                    ? VisibilityContext.AllVisible
                    : new VisibilityContext(RubyMethodAttributes.Public);

                var method = cls.ResolveMethodWithRefinements(methodName, visibility, scope);
                if (method.Found) {
                    return IsImplemented(method.Info);
                }
            }

            // MRI asks respond_to_missing? before giving up, so that method_missing-backed methods can
            // advertise themselves. Note that the protocol-conversion binder has a fast path that bypasses
            // Kernel#respond_to? altogether, so a conversion method advertised this way is still not seen there.
            //
            // A BasicObject subclass that borrowed only #respond_to?, or a class that undefined
            // #respond_to_missing?, has nothing to ask; MRI answers false rather than raising. Nor is
            // Kernel's own respond_to_missing? called: it answers false, and MRI's basic_obj_respond_to
            // skips it the same way (rb_method_basic_definition_p). Only the *lookup* is cached - a
            // user-defined respond_to_missing? is called every time.
            int missingVersion;
            var missing = cls.GetMethodLookup(Symbols.RespondToMissing, out missingVersion);
            if (!missing.Found || IsKernelRespondToMissing(missing.Info)) {
                if (missingVersion == version) {
                    respondToMissingStorage.SetAnswer(cls, version, methodName, includePrivate, false);
                }
                return false;
            }

            var site = respondToMissingStorage.GetCallSite(Symbols.RespondToMissing, 2);
            return Protocols.IsTrue(site.Target(site, self, context.StringifyIdentifier(methodName),
                ScriptingRuntimeHelpers.BooleanToObject(includePrivate)));
        }

        // MRI's rb_f_notimplement methods (fork on a platform without it) are defined but denied.
        private static bool IsImplemented(RubyMemberInfo/*!*/ method) {
            var libraryMethod = method as RubyLibraryMethodInfo;
            return libraryMethod == null || !libraryMethod.IsNotImplemented;
        }

        private static readonly MethodInfo/*!*/ _respondToMissingMethod = typeof(KernelOps).GetMethod("RespondToMissing");

        // The last method info found to be Kernel#respond_to_missing? itself (or an alias of it).
        private static RubyMemberInfo _defaultRespondToMissing;

        private static bool IsKernelRespondToMissing(RubyMemberInfo/*!*/ method) {
            if (ReferenceEquals(method, _defaultRespondToMissing)) {
                return true;
            }

            var libraryMethod = method as RubyLibraryMethodInfo;
            if (libraryMethod == null) {
                return false;
            }

            var members = libraryMethod.GetMembers();
            if (members.Length == 1 && members[0] == _respondToMissingMethod) {
                _defaultRespondToMissing = method;
                return true;
            }
            return false;
        }

        [RubyMethod("respond_to_missing?", RubyMethodAttributes.PrivateInstance)]
        public static bool RespondToMissing(object self, object methodName, object includePrivate) {
            return false;
        }

        #endregion

        #region send, public_send

        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, object self) {
            throw RubyExceptions.CreateArgumentError("no method name given");
        }

        // Its own method rather than a second name on the one above, so that the frame in the
        // backtrace is called public_send: the stack trace takes the first [RubyMethod] name a
        // method carries, and ruby/spec looks for that name.
        [RubyMethod("public_send")]
        public static object PublicSendMessage(RubyScope/*!*/ scope, object self) {
            throw RubyExceptions.CreateArgumentError("no method name given");
        }

        // public_send differs from send only in visibility: the site sees Ruby-public members
        // and nothing else. Leaving self explicit is not enough on its own - the site still
        // carries the scope, for refinements, and a scope-based visibility check makes a
        // protected method visible to a receiver of the caller's own class.
        //
        // The name is converted here rather than by the binder, so that a bad one is reported
        // from inside public_send, as MRI's backtrace has it.
        // obj.public_send(:name): no argument list to make and splat again
        [RubyMethod("public_send")]
        public static object PublicSendMessage(ConversionStorage<string>/*!*/ stringCast,
            SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object>>/*!*/ sendStorage,
            RubyScope/*!*/ scope, BlockParam block, object self, object name) {

            string methodName = ToVariableName(stringCast, name);
            var site = GetSendSite(sendStorage, scope, methodName, new RubyCallSignature(0,
                RubyCallFlags.HasScope | RubyCallFlags.HasBlock | RubyCallFlags.IsInteropCall)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null);
        }

        [RubyMethod("public_send")]
        [ReceivesKeywords]
        public static object PublicSendMessage(ConversionStorage<string>/*!*/ stringCast,
            SendSiteStorage<Func<CallSite, RubyScope, object, Proc, RubyArray, object>>/*!*/ sendStorage,
            RubyScope/*!*/ scope, BlockParam block, object self, object name, params object[]/*!*/ args) {

            string methodName = ToVariableName(stringCast, name);
            var site = GetSendSite(sendStorage, scope, methodName, new RubyCallSignature(1,
                RubyCallFlags.HasScope | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock | RubyCallFlags.IsInteropCall)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, RubyOps.MakeArrayN(args));
        }

        // ARGS: 0
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName) {
            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(0, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );
            return site.Target(site, scope, self);
        }

        // ARGS: 0&
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName) {
            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(0, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null);
        }

        // ARGS: 1
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1) {

            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );

            return site.Target(site, scope, self, arg1);
        }

        // ARGS: 1&
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1) {

            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, arg1);
        }

        // ARGS: 2
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2) {

            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(2, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );

            return site.Target(site, scope, self, arg1, arg2);
        }

        // ARGS: 2&
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2) {

            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(2, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, arg1, arg2);
        }

        // ARGS: 3
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2, object arg3) {

            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(3, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );

            return site.Target(site, scope, self, arg1, arg2, arg3);
        }

        // ARGS: 3&
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2, object arg3) {

            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(3, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, arg1, arg2, arg3);
        }

        // ARGS: N
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            params object[]/*!*/ args) {
            
            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasSplattedArgument)
            );

            return site.Target(site, scope, self, RubyOps.MakeArrayN(args));
        }

        // ARGS: N&
        [RubyMethod("send")]
        [ReceivesKeywords]
        public static object SendMessage(SendSiteStorage<Func<CallSite, RubyScope, object, Proc, RubyArray, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            params object[]/*!*/ args) {

            var site = GetSendSite(storage, scope, methodName,
                new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasSplattedArgument |
                    RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, RubyOps.MakeArrayN(args));
        }

        // The site a send dispatches through: the send site's own (its storage), or - for a send made
        // from library code, which has no site of its own - the context's shared one.
        private static CallSite<TSiteFunc>/*!*/ GetSendSite<TSiteFunc>(SendSiteStorage<TSiteFunc> storage, RubyScope/*!*/ scope,
            string/*!*/ methodName, RubyCallSignature signature) where TSiteFunc : class {

            return (storage != null)
                ? storage.GetCallSite(methodName, signature)
                : scope.RubyContext.GetOrCreateSendSite<TSiteFunc>(methodName, signature);
        }

        internal static object SendMessageOpt(RubyScope/*!*/ scope, BlockParam block, object self, string/*!*/ methodName, object[] args) {
            switch ((args != null ? args.Length : 0)) {
                case 0: return SendMessage((SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object>>)null,
                    scope, block, self, methodName);
                case 1: return SendMessage((SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object>>)null,
                    scope, block, self, methodName, args[0]);
                case 2: return SendMessage((SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object, object>>)null,
                    scope, block, self, methodName, args[0], args[1]);
                case 3: return SendMessage((SendSiteStorage<Func<CallSite, RubyScope, object, Proc, object, object, object, object>>)null,
                    scope, block, self, methodName, args[0], args[1], args[2]);
                default: return SendMessage((SendSiteStorage<Func<CallSite, RubyScope, object, Proc, RubyArray, object>>)null,
                    scope, block, self, methodName, args);
            }
        }

        // 1.9: public_send

        #endregion

        #region clr_member, method, public_method

        // thread-safe:
        /// <summary>
        /// Returns a RubyMethod instance that represents one or more CLR members of given name.
        /// An exception is thrown if the member is not found.
        /// Name could be of Ruby form (foo_bar) or CLR form (FooBar). Operator names are translated 
        /// (e.g. "+" to op_Addition, "[]"/"[]=" to a default index getter/setter).
        /// The resulting RubyMethod might represent multiple CLR members (overloads).
        /// Inherited members are included.
        /// Includes all CLR members that match the name even if they are not callable from Ruby - 
        /// they are hidden by a Ruby member or their declaring type is not included in the ancestors list of the class.
        /// Includes members of any Ruby visibility.
        /// Includes CLR protected members.
        /// Includes CLR private members if PrivateBinding is on.
        /// </summary>
        [RubyMethod("clr_member")]
        public static RubyMethod/*!*/ GetClrMember(RubyContext/*!*/ context, object self, [DefaultParameterValue(null), NotNull]object asType, 
            [DefaultProtocol, NotNull]string/*!*/ name) {
            RubyMemberInfo info;

            RubyClass cls = context.GetClassOf(self);
            Type type = (asType != null) ? Protocols.ToType(context, asType) : null;
            if (!cls.TryGetClrMember(name, type, out info)) {
                throw RubyExceptions.CreateNameError("undefined CLR method '{0}' for class '{1}'", name, cls.Name);
            }

            return new RubyMethod(self, info, name);
        }

        // thread-safe:
        [RubyMethod("method")]
        public static RubyMethod/*!*/ GetMethod(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ respondToMissingStorage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ name) {

            var context = scope.RubyContext;

            // A refinement active where #method was called is active for it: MRI hands back the
            // refined method, whose #owner is the refinement.
            RubyMemberInfo info = context.ResolveMethodWithRefinements(self, name, VisibilityContext.AllVisible, scope).Info;
            if (info != null) {
                return new RubyMethod(self, info, name);
            }

            // MRI asks respond_to_missing? before giving up: an object may advertise a name that
            // only method_missing implements, and then #method has to hand back something that
            // calls method_missing with that name
            var site = respondToMissingStorage.GetCallSite("respond_to_missing?", 2);
            if (Protocols.IsTrue(site.Target(site, self, context.StringifyIdentifier(name),
                ScriptingRuntimeHelpers.BooleanToObject(true)))) {

                var missing = context.ResolveMethod(self, Symbols.MethodMissing, VisibilityContext.AllVisible).Info;
                if (missing != null) {
                    return RubyMethod.CreateMethodMissing(self, missing, name);
                }
            }

            throw RubyExceptions.CreateUndefinedMethodError(context.GetClassOf(self), name);
        }

        /// <summary>
        /// rb_obj_public_method: #method restricted to public methods. A private or protected
        /// method is reported as undefined, but #method_missing and #respond_to_missing? are
        /// consulted exactly as #method consults them. In C# rather than in the prelude because
        /// a refinement is active for the lexical place the call was written, and a Ruby wrapper
        /// would ask from the prelude's place instead.
        /// </summary>
        [RubyMethod("public_method")]
        public static RubyMethod/*!*/ GetPublicMethod(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ respondToMissingStorage,
            RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ name) {

            var context = scope.RubyContext;
            RubyMemberInfo info = context.ResolveMethodWithRefinements(self, name, VisibilityContext.AllVisible, scope).Info;

            if (info != null) {
                if (info.Visibility == RubyMethodVisibility.Public) {
                    return new RubyMethod(self, info, name);
                }
                throw RubyExceptions.CreateNameError("method '{0}' for class '{1}' is {2}",
                    name, context.GetClassDisplayName(self),
                    info.Visibility == RubyMethodVisibility.Private ? "private" : "protected");
            }

            // Nothing of that name is defined, so only a public #respond_to_missing? can still
            // produce one - asked with include_private false, unlike #method, which asks true.
            var site = respondToMissingStorage.GetCallSite("respond_to_missing?", 2);
            if (Protocols.IsTrue(site.Target(site, self, context.StringifyIdentifier(name),
                ScriptingRuntimeHelpers.BooleanToObject(false)))) {

                var missing = context.ResolveMethod(self, Symbols.MethodMissing, VisibilityContext.AllVisible).Info;
                if (missing != null) {
                    return RubyMethod.CreateMethodMissing(self, missing, name);
                }
            }

            throw RubyExceptions.CreateUndefinedMethodError(context.GetClassOf(self), name);
        }

        /// <summary>
        /// `class &lt;&lt; obj; self; end' as a method. In C# rather than in the prelude so that a
        /// warning raised on the way - a chilled string literal being given a singleton class is
        /// one - names the caller's line and not the prelude's.
        /// </summary>
        [RubyMethod("singleton_class")]
        public static RubyClass/*!*/ GetSingletonClass(RubyScope/*!*/ scope, object self) {
            return RubyOps.DefineSingletonClass(scope, self);
        }

        // 1.9: public: public_method

        #endregion

        #region define_singleton_method (thread-safe)

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubySymbol/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {

            RubyUtils.RequireDefinableSingleton(self);
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, method);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubySymbol/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {

            RubyUtils.RequireDefinableSingleton(self);
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, method);
        }

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubySymbol/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {

            RubyUtils.RequireDefinableSingleton(self);
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, method);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubySymbol/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {

            RubyUtils.RequireDefinableSingleton(self);
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, method);
        }

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubySymbol/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block,
            object self, [DefaultProtocol, NotNull]string/*!*/ methodName) {

            RubyUtils.RequireDefinableSingleton(self);
            return ModuleOps.DefineMethod(scope, block, scope.RubyContext.GetOrCreateSingletonClass(self), methodName);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubySymbol/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block,
            object self, [NotNull]ClrName/*!*/ methodName) {

            RubyUtils.RequireDefinableSingleton(self);
            return ModuleOps.DefineMethod(scope, block, scope.RubyContext.GetOrCreateSingletonClass(self), methodName);
        }

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubySymbol/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]Proc/*!*/ block) {

            RubyUtils.RequireDefinableSingleton(self);
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, block);
        }

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubySymbol/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]Proc/*!*/ block) {

            RubyUtils.RequireDefinableSingleton(self);
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, block);
        }

        #endregion


        #region methods, (private|protected|public|singleton)_methods (thread-safe)

        // thread-safe:
        [RubyMethod("methods")]
        public static RubyArray/*!*/ GetMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            var foreignMembers = context.GetForeignDynamicMemberNames(self);

            RubyClass immediateClass = context.GetImmediateClassOf(self);
            if (!inherited && !immediateClass.IsSingletonClass) {
                var result = new RubyArray();
                if (foreignMembers.Count > 0) {
                    foreach (var name in foreignMembers) {
	                    if (Tokenizer.IsMethodName(name) || Tokenizer.IsOperatorName(name)) {
                            result.Add(new ClrName(name));
                        }
                    }
                }
                return result;
            }

            return ModuleOps.GetMethods(immediateClass, inherited, RubyMethodAttributes.Public | RubyMethodAttributes.Protected, foreignMembers);
        }

        // thread-safe:
        [RubyMethod("singleton_methods")]
        public static RubyArray/*!*/ GetSingletonMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            RubyClass immediateClass = context.GetImmediateClassOf(self);
            return ModuleOps.GetMethods(immediateClass, inherited, RubyMethodAttributes.Singleton | RubyMethodAttributes.Public | RubyMethodAttributes.Protected);
        }

        // thread-safe:
        [RubyMethod("private_methods")]
        public static RubyArray/*!*/ GetPrivateMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            return GetMethods(context, self, inherited, RubyMethodAttributes.PrivateInstance);
        }

        // thread-safe:
        [RubyMethod("protected_methods")]
        public static RubyArray/*!*/ GetProtectedMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            return GetMethods(context, self, inherited, RubyMethodAttributes.ProtectedInstance);
        }

        // thread-safe:
        [RubyMethod("public_methods")]
        public static RubyArray/*!*/ GetPublicMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            return GetMethods(context, self, inherited, RubyMethodAttributes.PublicInstance);
        }

        private static RubyArray/*!*/ GetMethods(RubyContext/*!*/ context, object self, bool inherited, RubyMethodAttributes attributes) {
            RubyClass immediateClass = context.GetImmediateClassOf(self);
            return ModuleOps.GetMethods(immediateClass, inherited, attributes, null, true);
        }

        #endregion


        #region `, exec, system, fork, 1.9: spawn

        // Kernel#`, #exec, #system and #spawn are in the prelude, on top of Process.spawn:
        // they are the same call with different treatment of the child, and the argument
        // handling they share - an env hash, a [command, argv0] pair, an options hash - is
        // all protocol work that belongs in Ruby.

        // There is no fork on the CLR. MRI defines the method on platforms that cannot fork too and has it
        // raise NotImplementedError; Process.respond_to?(:fork) is what portable code tests instead.
        // The method has to exist: fixtures such as spec/core/kernel/fixtures/classes.rb do `public :fork'.
        [RubyMethod("fork", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("fork", RubyMethodAttributes.PublicSingleton)]
        [RubyNotImplemented]
        public static object Fork(BlockParam block, object self) {
            throw RubyExceptions.CreateNotImplementedError("fork() function is unimplemented on this machine");
        }

        #endregion

        #region select, sleep

        [RubyMethod("select", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("select", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Select(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, UnaryOpStorage/*!*/ toFloatStorage,
            RubyContext/*!*/ context, object self,
            object read, [Optional]object write, [Optional]object error, [Optional]object timeout) {
            return RubyIOOps.Select(respondToStorage, toIoStorage, toFloatStorage, context, null, read, write, error, timeout);
        }

        [RubyMethod("sleep", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("sleep", RubyMethodAttributes.PublicSingleton)]
        public static void Sleep(object self) {
            ThreadOps.DoSleep();
        }

        [RubyMethod("sleep", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("sleep", RubyMethodAttributes.PublicSingleton)]
        public static int Sleep(object self, int seconds) {
            if (seconds < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }

            long ms = seconds * 1000L;
            return ThreadOps.DoSleep(ms > Int32.MaxValue ? Timeout.Infinite : (int)ms);
        }

        [RubyMethod("sleep", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("sleep", RubyMethodAttributes.PublicSingleton)]
        public static int Sleep(object self, double seconds) {
            // MRI's rb_time_interval
            if (Double.IsNaN(seconds)) {
                throw RubyExceptions.CreateRangeError("NaN out of Time range");
            }
            if (seconds < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }

            double ms = seconds * 1000;
            return ms > Int32.MaxValue ? ThreadOps.DoSleep(Timeout.Infinite) : ThreadOps.DoSleep(ms);
        }

        [RubyMethod("sleep", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("sleep", RubyMethodAttributes.PublicSingleton)]
        public static int Sleep(RubyContext/*!*/ context, object self, object seconds) {
            switch (seconds) {
                case null:
                    // A nil duration (3.3) is the same as none: sleep until woken.
                    ThreadOps.DoSleep();
                    return 0;
                case BigInteger:
                    throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long'");
                default:
                    // Numerics that answer #divmod (Rational) are split up by the prelude's Kernel#sleep.
                    throw RubyExceptions.CreateTypeError("can't convert {0} into time interval", context.GetClassDisplayName(seconds));
            }
        }

        #endregion

        #region test
        [RubyMethod("test", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("test", RubyMethodAttributes.PublicSingleton)]
        public static object Test(ConversionStorage<MutableString>/*!*/ toPath, object self, [NotNull]MutableString/*!*/ cmd, object path) {
            if (cmd.IsEmpty) {
                throw RubyExceptions.CreateTypeConversionError("String", "Integer");
            }
            return Test(toPath, self, cmd.GetChar(0), path);
        }

        [RubyMethod("test", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("test", RubyMethodAttributes.PublicSingleton)]
        public static object Test(ConversionStorage<MutableString>/*!*/ toPath, object self, [DefaultProtocol]int cmd, object path) {
            RubyContext context = toPath.Context;
            MutableString pathStr = Protocols.CastToPath(toPath, path);
            cmd &= 0xFF;
            switch (cmd) {
                // The type and permission questions all answer false for a path that cannot be
                // stat'd, the way FileTest's predicates do; only the three time queries raise.
                case 'b': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsBlockDevice);
                case 'c': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsCharDevice);
                case 'd': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsDirectory);
                case 'e': return Stat(context, pathStr, true, (fsi) => true);
                case 'f': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsFile);
                case 'g': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsSetGid);
                case 'G': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsGroupOwned);
                case 'k': return Stat(context, pathStr, true, (fsi) => RubyFileOps.RubyStatOps.IsSticky(fsi) as bool? ?? false);
                // ?l asks about the link itself, so it is the one query that must not follow it.
                case 'l': return Stat(context, pathStr, false, RubyFileOps.RubyStatOps.IsSymLink);
                case 'o': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsUserOwned);
                case 'O': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsUserOwnedReal);
                case 'p': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsPipe);
                case 'r': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsReadable);
                case 'R': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsReadableReal);
                case 'S': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsSocket);
                case 'u': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsSetUid);
                case 'w': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsWritable);
                case 'W': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsWritableReal);
                case 'x': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsExecutable);
                case 'X': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsExecutableReal);
                case 'z': return Stat(context, pathStr, true, RubyFileOps.RubyStatOps.IsZeroLength);

                // ?s is the size, or nil for an empty file - and nil, not an error, for one that
                // is not there.
                case 's': {
                    FileSystemInfo fsi = TryStat(context, pathStr, true);
                    return fsi != null ? RubyFileOps.RubyStatOps.NullableSize(fsi) : null;
                }

                case 'A': return RubyFileOps.RubyStatOps.AccessTime(RubyFileOps.RubyStatOps.Create(context, pathStr));
                case 'C': return RubyFileOps.RubyStatOps.CreateTime(RubyFileOps.RubyStatOps.Create(context, pathStr));
                case 'M': return RubyFileOps.RubyStatOps.ModifiedTime(RubyFileOps.RubyStatOps.Create(context, pathStr));

                default:
                    throw RubyExceptions.CreateArgumentError("unknown command '{0}'", (char)cmd);
            }
        }

        [RubyMethod("test", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("test", RubyMethodAttributes.PublicSingleton)]
        public static object Test(ConversionStorage<MutableString>/*!*/ toPath, object self, [NotNull]MutableString/*!*/ cmd,
            object path1, object path2) {

            if (cmd.IsEmpty) {
                throw RubyExceptions.CreateTypeConversionError("String", "Integer");
            }
            return Test(toPath, self, cmd.GetChar(0), path1, path2);
        }

        [RubyMethod("test", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("test", RubyMethodAttributes.PublicSingleton)]
        public static object Test(ConversionStorage<MutableString>/*!*/ toPath, object self, [DefaultProtocol]int cmd,
            object path1, object path2) {

            RubyContext context = toPath.Context;
            cmd &= 0xFF;
            if (cmd != '-' && cmd != '=' && cmd != '<' && cmd != '>') {
                throw RubyExceptions.CreateArgumentError("unknown command '{0}'", (char)cmd);
            }

            FileSystemInfo first = TryStat(context, Protocols.CastToPath(toPath, path1), true);
            FileSystemInfo second = TryStat(context, Protocols.CastToPath(toPath, path2), true);
            if (first == null || second == null) {
                return false;
            }

            if (cmd == '-') {
                // "is a hard link to": the same device and inode, which is what File.identical?
                // answers as well.
                return RubyFileOps.RubyStatOps.AreIdentical(context, first, second);
            }

            int comparison = RubyFileOps.RubyStatOps.ModifiedTime(first).CompareTo(RubyFileOps.RubyStatOps.ModifiedTime(second));
            return cmd == '=' ? comparison == 0 : (cmd == '<' ? comparison < 0 : comparison > 0);
        }

        /// <summary>The file's stat, or null if it cannot be taken - a missing path, most often.</summary>
        private static FileSystemInfo TryStat(RubyContext/*!*/ context, MutableString/*!*/ path, bool followLinks) {
            FileSystemInfo fsi;
            int errno;
            return RubyFileOps.RubyStatOps.TryCreate(context, context.DecodePath(path), followLinks, out fsi, out errno) ? fsi : null;
        }

        private static bool Stat(RubyContext/*!*/ context, MutableString/*!*/ path, bool followLinks, Func<FileSystemInfo, bool>/*!*/ predicate) {
            FileSystemInfo fsi = TryStat(context, path, followLinks);
            return fsi != null && predicate(fsi);
        }
        #endregion

        #region syscall, trap

        // A raw system call by number cannot be made portably from .NET; this is what MRI answers
        // on a platform without syscall(2).
        [RubyMethod("syscall", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("syscall", RubyMethodAttributes.PublicSingleton)]
        public static object Syscall(object self, params object[]/*!*/ args) {
            throw RubyExceptions.CreateNotImplementedError("syscall() function is unimplemented on this machine");
        }

        [RubyMethod("trap", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("trap", RubyMethodAttributes.PublicSingleton)]
        public static object Trap(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            RubyContext/*!*/ context, object self, object signalId, object command) {
            return Signal.Trap(callStorage, stringCast, context, self, signalId, command);
        }

        [RubyMethod("trap", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("trap", RubyMethodAttributes.PublicSingleton)]
        public static object Trap(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, object self, object signalId) {
            return Signal.Trap(callStorage, stringCast, context, block, self, signalId);
        }

        #endregion

        #region abort, exit, exit!, at_exit

        [RubyMethod("abort", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("abort", RubyMethodAttributes.PublicSingleton)]
        public static void Abort(object/*!*/ self) {
            Exit(self, 1);
        }

        [RubyMethod("abort", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("abort", RubyMethodAttributes.PublicSingleton)]
        public static void Abort(BinaryOpStorage/*!*/ writeStorage, object/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ message) {
            // MRI writes the message as a line, and the SystemExit it raises carries
            // that message rather than the plain "exit" that a bare exit uses.
            var line = message.Clone();
            if (!line.EndsWith('\n')) {
                line.Append((byte)'\n');
            }
            var site = writeStorage.GetCallSite("write", 1);
            site.Target(site, writeStorage.Context.StandardErrorOutput, line);

            throw new SystemExit(1, message.ConvertToString());
        }

        [RubyMethod("exit", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
        public static void Exit(object self) {
            Exit(self, 0);
        }

        [RubyMethod("exit", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
        public static void Exit(object self, [NotNull]bool isSuccessful) {
            Exit(self, isSuccessful ? 0 : 1);
        }

        [RubyMethod("exit", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
        public static void Exit(object self, [DefaultProtocol]int exitCode) {
            throw new SystemExit(exitCode, "exit");
        }

        [RubyMethod("exit!", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit!", RubyMethodAttributes.PublicSingleton)]
        public static void TerminateExecution(RubyContext/*!*/ context, object self) {
            TerminateExecution(context, self, 1);
        }

        [RubyMethod("exit!", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit!", RubyMethodAttributes.PublicSingleton)]
        public static void TerminateExecution(RubyContext/*!*/ context, object self, bool isSuccessful) {
            TerminateExecution(context, self, isSuccessful ? 0 : 1);
        }

        [RubyMethod("exit!", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit!", RubyMethodAttributes.PublicSingleton)]
        public static void TerminateExecution(RubyContext/*!*/ context, object self, int exitCode) {
            context.DomainManager.Platform.TerminateScriptExecution(exitCode);
        }

        [RubyMethod("at_exit", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("at_exit", RubyMethodAttributes.PublicSingleton)]
        public static Proc/*!*/ AtExit(BlockParam/*!*/ block, object self) {
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("called without a block");
            }

            block.RubyContext.RegisterShutdownHandler(block.Proc);
            return block.Proc;
        }

        #endregion

        #region load, load_assembly, require, using_clr_extensions

        [RubyMethod("load", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        public static bool Load(ConversionStorage<MutableString>/*!*/ toPath, RubyScope/*!*/ scope, object self, object libraryName) {
            return Load(toPath, scope, self, libraryName, null);
        }

        /// <summary>
        /// `wrap` is true/false in every Ruby up to 3.0 and may be a Module from 3.1, which then
        /// becomes the enclosing scope instead of a fresh anonymous one. A separate arity rather
        /// than an optional parameter: an omitted [Optional]object arrives as Missing.Value, not
        /// as nil, and would read as truthy.
        /// </summary>
        [RubyMethod("load", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        public static bool Load(ConversionStorage<MutableString>/*!*/ toPath, RubyScope/*!*/ scope, object self, object libraryName, object wrap) {
            RubyModule wrapModule = wrap as RubyModule;
            bool isolated = wrapModule != null || (wrap != null && !(wrap is bool && !(bool)wrap));

            RubyTopLevelScope.PendingWrapModule = wrapModule;
            try {
                return scope.RubyContext.Loader.LoadFile(
                    scope.GlobalScope.Scope,
                    self,
                    Protocols.CastToPath(toPath, libraryName),
                    isolated ? LoadFlags.LoadIsolated : LoadFlags.None
                );
            } finally {
                RubyTopLevelScope.PendingWrapModule = null;
            }
        }

        [RubyMethod("require", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("require", RubyMethodAttributes.PublicSingleton)]
        public static bool Require(ConversionStorage<MutableString>/*!*/ toPath, RubyScope/*!*/ scope, object self, object libraryName) {
            var path = Protocols.CastToPath(toPath, libraryName);
            // a non-main Ractor has the main Ractor load the file (Ractor._require)
            return RactorIsolation.IsNonMain(scope.RubyContext) ? RequireInMainRactor(scope, self, path) : Require(scope, self, path);
        }

        private static bool Require(RubyScope/*!*/ scope, object self, MutableString/*!*/ path) {
            return scope.RubyContext.Loader.LoadFile(scope.GlobalScope.Scope, self, path, LoadFlags.Require | LoadFlags.WarnCircular);
        }

        private static bool RequireInMainRactor(RubyScope/*!*/ scope, object self, MutableString/*!*/ path) {
            return RubyRactor.RunAsMain(() => Require(scope, self, path));
        }

        [RubyMethod("load_assembly", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("load_assembly", RubyMethodAttributes.PublicSingleton)]
        public static bool LoadAssembly(RubyContext/*!*/ context, object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ assemblyName, [DefaultProtocol, Optional, NotNull]MutableString libraryNamespace) {

            string initializer = libraryNamespace != null ? LibraryInitializer.GetFullTypeName(libraryNamespace.ConvertToString()) : null;
            return context.Loader.LoadAssembly(assemblyName.ConvertToString(), initializer, true) != null;
        }

        [RubyMethod("using_clr_extensions", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("using_clr_extensions", RubyMethodAttributes.PublicSingleton)]
        public static void UsingClrExtensions(RubyContext/*!*/ context, object self, RubyModule namespaceModule) {
            string ns;
            if (namespaceModule == null) {
                ns = "";
            } else if (namespaceModule.NamespaceTracker == null) {
                throw RubyExceptions.CreateNotClrNamespaceError(namespaceModule);
            } else if (context != namespaceModule.Context) {
                throw RubyExceptions.CreateTypeError("Cannot use namespace '{0}' defined in a foreign runtime #{1}",
                    namespaceModule.NamespaceTracker.Name, namespaceModule.Context.RuntimeId);
            } else {
                ns = namespaceModule.NamespaceTracker.Name;
            }

            context.ReportUsingClrExtensionsDeprecation(namespaceModule != null ? namespaceModule.Name : null);
            context.ActivateExtensions(ns);
        }

        #endregion

        #region open

        // TODO: should call File#initialize

        private static object OpenWithBlock(BlockParam/*!*/ block, RubyIO file) {
            try {
                object result;
                block.Yield(file, out result);
                return result;
            } finally {
                file.Close();
            }
        }

        private static void SetPermission(RubyContext/*!*/ context, string/*!*/ fileName, int/*!*/ permission) {
            bool existingFile = context.DomainManager.Platform.FileExists(fileName);

            if (!existingFile) {
                RubyFileOps.Chmod(fileName, permission);
            }
        }
        private static RubyIO CheckOpenPipe(RubyContext/*!*/ context, MutableString path, IOMode mode) {
            string fileName = path.ConvertToString();
            if (fileName.Length > 0 && fileName[0] == '|') {
                if (fileName.Length > 1 && fileName[1] == '-') {
                    throw new NotImplementedError("forking a process is not supported");
                }
                return RubyIOOps.OpenPipe(context, path.GetSlice(1), mode);
            }
            return null;
        }

        [RubyMethod("open", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RubyIO/*!*/ Open(
            RubyContext/*!*/ context,
            object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path,
            [DefaultProtocol, Optional]MutableString modeString,
            [DefaultProtocol, DefaultParameterValue(RubyFileOps.ReadWriteMode)]int permission) {

            IOMode mode = IOModeEnum.Parse(modeString);
            
            RubyIO pipe = CheckOpenPipe(context, path, mode);
            if (pipe != null) {
                return pipe;
            }

            string fileName = path.ConvertToString();
            RubyIO file = new RubyFile(context, fileName, mode);

            SetPermission(context, fileName, permission);

            return file;
        }

        [RubyMethod("open", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static object Open(
            RubyContext/*!*/ context,
            [NotNull]BlockParam/*!*/ block,
            object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path,
            [DefaultProtocol, Optional]MutableString mode,
            [DefaultProtocol, DefaultParameterValue(RubyFileOps.ReadWriteMode)]int permission) {

            RubyIO file = Open(context, self, path, mode, permission);
            return OpenWithBlock(block, file);
        }

        [RubyMethod("open", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RubyIO/*!*/ Open(
            RubyContext/*!*/ context,
            object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path,
            int mode,
            [DefaultProtocol, DefaultParameterValue(RubyFileOps.ReadWriteMode)]int permission) {

            RubyIO pipe = CheckOpenPipe(context, path, IOModeNative.ToIOMode(mode));
            if (pipe != null) {
                return pipe;
            }

            string fileName = path.ConvertToString();
            RubyIO file = new RubyFile(context, fileName, IOModeNative.ToIOMode(mode));

            SetPermission(context, fileName, permission);

            return file;
        }

        [RubyMethod("open", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static object Open(
            RubyContext/*!*/ context,
            [NotNull]BlockParam/*!*/ block,
            object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path,
            int mode,
            [DefaultProtocol, DefaultParameterValue(RubyFileOps.ReadWriteMode)]int permission) {

            RubyIO file = Open(context, self, path, mode, permission);
            return OpenWithBlock(block, file);
        }

        #endregion

        #region p, print, printf, putc, puts, display, warn, gets, getc

        [RubyMethod("p", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("p", RubyMethodAttributes.PublicSingleton)]
        public static object PrintInspect(BinaryOpStorage/*!*/ writeStorage, UnaryOpStorage/*!*/ inspectStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            object self, params object[]/*!*/ args) {

            var inspect = inspectStorage.GetCallSite("inspect");
            var inspectedArgs = new MutableString[args.Length];
            for (int i = 0; i < args.Length; i++) {
                inspectedArgs[i] = Protocols.ConvertToString(tosConversion, inspect.Target(inspect, args[i]));
            }

            // no dynamic dispatch to "puts":
            foreach (var arg in inspectedArgs) {
                PrintOps.Puts(writeStorage, writeStorage.Context.StandardOutput, arg);
            }

            // rb_f_p flushes $stdout when it is an IO, so the output is visible to a reader at once
            var stdout = writeStorage.Context.StandardOutput as RubyIO;
            if (args.Length > 0 && stdout != null) {
                RubyIOOps.Flush(stdout);
            }

            if (args.Length == 0) {
                return null;
            } else if (args.Length == 1) {
                return args[0];
            } else {
                return new RubyArray(args);
            }
        }

        [RubyMethod("print", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("print", RubyMethodAttributes.PublicSingleton)]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, RubyScope/*!*/ scope, object self) {
            // no dynamic dispatch to "print":
            PrintOps.Print(writeStorage, scope, scope.RubyContext.StandardOutput);
        }

        [RubyMethod("print", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("print", RubyMethodAttributes.PublicSingleton)]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object self, object val) {
            // no dynamic dispatch to "print":
            PrintOps.Print(writeStorage, writeStorage.Context.StandardOutput, val);
        }

        [RubyMethod("print", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("print", RubyMethodAttributes.PublicSingleton)]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object self, params object[]/*!*/ args) {
            // no dynamic dispatch to "print":
            PrintOps.Print(writeStorage, writeStorage.Context.StandardOutput, args);
        }

        // MRI's rb_f_printf prints nothing for no arguments
        [RubyMethod("printf", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("printf", RubyMethodAttributes.PublicSingleton)]
        public static void PrintFormatted(object self) {
        }

        // this overload is called only if the first parameter is string:
        [RubyMethod("printf", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("printf", RubyMethodAttributes.PublicSingleton)]
        public static void PrintFormatted(
            StringFormatterSiteStorage/*!*/ storage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            BinaryOpStorage/*!*/ writeStorage,
            object self, [NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {

            PrintFormatted(storage, stringCast, writeStorage, self, storage.Context.StandardOutput, format, args);
        }

        [RubyMethod("printf", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("printf", RubyMethodAttributes.PublicSingleton)]
        public static void PrintFormatted(
            StringFormatterSiteStorage/*!*/ storage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            BinaryOpStorage/*!*/ writeStorage,
            object self, object io, [NotNull]object/*!*/ format, params object[]/*!*/ args) {

            Debug.Assert(!(io is MutableString));

            // TODO: BindAsObject attribute on format?
            // format cannot be strongly typed to MutableString due to ambiguity between signatures (MS, object) vs (object, MS)
            Protocols.Write(writeStorage, io,
                Sprintf(storage, self, Protocols.CastToString(stringCast, format), args)
            );
        }

        [RubyMethod("putc", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("putc", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Putc(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ arg) {
            // no dynamic dispatch:
            return PrintOps.Putc(writeStorage, writeStorage.Context.StandardOutput, arg);
        }

        [RubyMethod("putc", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("putc", RubyMethodAttributes.PublicSingleton)]
        public static object Putc(ConversionStorage<int>/*!*/ fixnumCast, BinaryOpStorage/*!*/ writeStorage, object self, object arg) {
            // no dynamic dispatch:
            return PrintOps.Putc(fixnumCast, writeStorage, writeStorage.Context.StandardOutput, arg);
        }

        [RubyMethod("puts", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("puts", RubyMethodAttributes.PublicSingleton)]
        public static void PutsEmptyLine(BinaryOpStorage/*!*/ writeStorage, object self) {
            // call directly, no dynamic dispatch to "self":
            PrintOps.PutsEmptyLine(writeStorage, writeStorage.Context.StandardOutput);
        }

        [RubyMethod("puts", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("puts", RubyMethodAttributes.PublicSingleton)]
        public static void PutString(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            ConversionStorage<IList>/*!*/ tryToAry, object self, object arg) {

            // call directly, no dynamic dispatch to "self":
            PrintOps.Puts(writeStorage, tosConversion, tryToAry, writeStorage.Context.StandardOutput, arg);
        }

        [RubyMethod("puts", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("puts", RubyMethodAttributes.PublicSingleton)]
        public static void PutString(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ arg) {
            // call directly, no dynamic dispatch to "self":
            PrintOps.Puts(writeStorage, writeStorage.Context.StandardOutput, arg);
        }

        [RubyMethod("puts", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("puts", RubyMethodAttributes.PublicSingleton)]
        public static void PutString(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            ConversionStorage<IList>/*!*/ tryToAry, object self, params object[]/*!*/ args) {

            // call directly, no dynamic dispatch to "self":
            PrintOps.Puts(writeStorage, tosConversion, tryToAry, writeStorage.Context.StandardOutput, args);
        }

        /// <summary>
        /// Ruby 4.0's hook for naming the instance variables #inspect shows: nil, all of them.
        /// RubyUtils.InspectObject does not call this one, which would make every #inspect pay for a call.
        /// </summary>
        [RubyMethod("instance_variables_to_inspect", RubyMethodAttributes.PrivateInstance)]
        public static object GetInstanceVariablesToInspect(object self) {
            return null;
        }

        [RubyMethod("display")]
        public static void Display(BinaryOpStorage/*!*/ writeStorage, object self) {
            Protocols.Write(writeStorage, writeStorage.Context.StandardOutput, self);
        }

        [RubyMethod("display")]
        public static void Display(BinaryOpStorage/*!*/ writeStorage, object self, object port) {
            Protocols.Write(writeStorage, port, self);
        }

        [RubyMethod("warn", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("warn", RubyMethodAttributes.PublicSingleton)]
        public static void ReportWarning(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            object self, object message) {

            PrintOps.ReportWarning(writeStorage, tosConversion, message);
        }

        // $_ is frame local, and the frame that has to see the line read is the caller's, not this
        // one. IO#gets does set it, but the scope it sets it in is the one at its own call site -
        // which here is inside this method, where nothing can read it again. So the assignment has
        // to be repeated against the scope Kernel#gets was called from.
        /// <summary>
        /// Kernel#gets reads ARGF, not $stdin: the files named in ARGV first, standard input only
        /// when there are none. Resolved per call because the prelude replaces the constant with
        /// its own ARGFClass instance after startup, so anything cached here would be the object
        /// that got thrown away.
        /// </summary>
        private static object GetArgFile(RubyContext/*!*/ context) {
            object argf;
            return context.ObjectClass.TryGetConstant(null, "ARGF", out argf) ? argf : context.StandardInput;
        }

        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object>>/*!*/ storage, RubyScope/*!*/ scope, object self) {
            var site = storage.GetCallSite("gets", 0);
            return scope.GetInnerMostClosureScope().LastInputLine = site.Target(site, GetArgFile(storage.Context));
        }

        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ storage, RubyScope/*!*/ scope, object self,
            [NotNull]MutableString/*!*/ separator) {

            var site = storage.GetCallSite("gets", 1);
            return scope.GetInnerMostClosureScope().LastInputLine = site.Target(site, GetArgFile(storage.Context), separator);
        }

        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ storage, RubyScope/*!*/ scope, object self,
            [NotNull]MutableString/*!*/ separator, [DefaultProtocol]int limit) {

            var site = storage.GetCallSite("gets", 2);
            return scope.GetInnerMostClosureScope().LastInputLine = site.Target(site, GetArgFile(storage.Context), separator, limit);
        }

        // gets(nil) reads the rest of the input: under -0777 -n the loop is `while gets($/)` with a nil $/.
        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ storage, RubyScope/*!*/ scope, object self,
            DynamicNull separator) {

            var site = storage.GetCallSite("gets", 1);
            return scope.GetInnerMostClosureScope().LastInputLine = site.Target(site, GetArgFile(storage.Context), null);
        }

        // gets(sep, chomp: true), which is also the loop -n -l runs: `while gets($/, chomp: true)`.
        // The line is chomped here: the keywords would not survive ARGF's Ruby-level *args.
        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ storage, RubyScope/*!*/ scope, object self,
            MutableString separator, [Keywords, NotNull]Hash/*!*/ options) {

            if (!options.IsKeywordArguments) {
                throw RubyExceptions.CreateImplicitConversionError("Hash", "Integer");
            }
            object chomp;
            bool doChomp = options.TryGetValue(storage.Context.CreateAsciiSymbol("chomp"), out chomp) && RubyOps.IsTrue(chomp);

            var site = storage.GetCallSite("gets", 1);
            object line = site.Target(site, GetArgFile(storage.Context), separator);
            if (doChomp && line is MutableString str) {
                RubyIOOps.ChompLine(str, separator);
            }
            return scope.GetInnerMostClosureScope().LastInputLine = line;
        }

        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ storage, RubyScope/*!*/ scope, object self,
            [Keywords, NotNull]Hash/*!*/ options) {
            return ReadInputLine(storage, scope, self, storage.Context.InputSeparator, options);
        }

        #endregion

        #region split, chomp, chop, gsub, sub, format, sprintf

        //split
        //chomp
        //chomp!
        //chop
        //chop!
        //gsub
        //gsub!
        //sub
        //sub!

        // Format errors are common, and MRI's backtrace names the method as it was called: Kernel#format,
        // Kernel#sprintf, Kernel.format or Kernel.sprintf. A backtrace here names a library frame by its CLR
        // method, so each is one of its own. NoInlining keeps the frame of the one-line ones.
        //
        // MRI: a missing format string is "too few arguments", not an arity error.
        [RubyMethod("format", RubyMethodAttributes.PrivateInstance)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("Sprintf")]
        public static MutableString/*!*/ Format(object self) {
            throw RubyExceptions.CreateArgumentError("too few arguments");
        }

        [RubyMethod("sprintf", RubyMethodAttributes.PrivateInstance)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static MutableString/*!*/ Sprintf(object self) {
            throw RubyExceptions.CreateArgumentError("too few arguments");
        }

        [RubyMethod("format", RubyMethodAttributes.PublicSingleton)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("Sprintf")]
        public static MutableString/*!*/ SingletonFormat(object self) {
            throw RubyExceptions.CreateArgumentError("too few arguments");
        }

        [RubyMethod("sprintf", RubyMethodAttributes.PublicSingleton)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("Sprintf")]
        public static MutableString/*!*/ SingletonSprintf(object self) {
            throw RubyExceptions.CreateArgumentError("too few arguments");
        }

        [RubyMethod("format", RubyMethodAttributes.PrivateInstance)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("Sprintf")]
        public static MutableString/*!*/ Format(StringFormatterSiteStorage/*!*/ storage,
            object self, [DefaultProtocol, NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {
            return Sprintf(storage, format, args);
        }

        [RubyMethod("sprintf", RubyMethodAttributes.PrivateInstance)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static MutableString/*!*/ Sprintf(StringFormatterSiteStorage/*!*/ storage,
            object self, [DefaultProtocol, NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {
            return Sprintf(storage, format, args);
        }

        [RubyMethod("format", RubyMethodAttributes.PublicSingleton)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("Sprintf")]
        public static MutableString/*!*/ SingletonFormat(StringFormatterSiteStorage/*!*/ storage,
            object self, [DefaultProtocol, NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {
            return Sprintf(storage, format, args);
        }

        [RubyMethod("sprintf", RubyMethodAttributes.PublicSingleton)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SameRubyMethod("Sprintf")]
        public static MutableString/*!*/ SingletonSprintf(StringFormatterSiteStorage/*!*/ storage,
            object self, [DefaultProtocol, NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {
            return Sprintf(storage, format, args);
        }

        internal static MutableString/*!*/ Sprintf(StringFormatterSiteStorage/*!*/ storage, MutableString/*!*/ format, object[]/*!*/ args) {
            // StringFormatter refuses a format in an ASCII incompatible encoding
            return new StringFormatter(storage, format.ConvertToString(), format.Encoding, args).Format();
        }

        #endregion

        #region rand, srand
        [RubyMethod("srand", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("srand", RubyMethodAttributes.PublicSingleton)]
        public static object SeedRandomNumberGenerator(RubyContext/*!*/ context, object self) {
            // This should use a combination of the time, the process id, and a sequence number.

            int secureRandomNumber = 0;
            do {
                byte[] b = new byte[4];
                RandomNumberGenerator.Fill(b);
                secureRandomNumber = ((int)b[0] << 24) | ((int)b[1] << 16) | ((int)b[2] << 8) | b[3];
            } while (secureRandomNumber == 0);
            return SeedRandomNumberGenerator(context, self, secureRandomNumber);
        }

        [RubyMethod("srand", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("srand", RubyMethodAttributes.PublicSingleton)]
        public static object SeedRandomNumberGenerator(RubyContext/*!*/ context, object self, [DefaultProtocol]IntegerValue seed) {
            object result = context.RandomNumberGeneratorSeed;
            context.SeedRandomNumberGenerator(seed);
            return result;
        }
        [RubyMethod("rand", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("rand", RubyMethodAttributes.PublicSingleton)]
        public static double Random(RubyContext/*!*/ context, object self) {
            return context.RandomNumberGenerator.NextDouble();
        }

        [RubyMethod("rand", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("rand", RubyMethodAttributes.PublicSingleton)]
        public static object Random(RubyContext/*!*/ context, object self, int limit) {
            Random generator = context.RandomNumberGenerator;
            if (limit == Int32.MinValue) {
                return generator.Random(-(BigInteger)limit);
            } else {
                return ScriptingRuntimeHelpers.Int32ToObject(generator.Next(limit));
            }
        }

        [RubyMethod("rand", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("rand", RubyMethodAttributes.PublicSingleton)]
        public static object Random(ConversionStorage<IntegerValue>/*!*/ conversion, RubyContext/*!*/ context, object self, object limit) {
            IntegerValue intLimit = Protocols.ConvertToInteger(conversion, limit);
            Random generator = context.RandomNumberGenerator;

            bool isFixnum;
            int fixnum = 0;
            BigInteger bignum = BigInteger.Zero;
            if (intLimit.IsFixnum) {
                if (intLimit.Fixnum == Int32.MinValue) {
                    bignum = -(BigInteger)intLimit.Fixnum;
                    isFixnum = false;
                } else {
                    fixnum = Math.Abs(intLimit.Fixnum);
                    isFixnum = true;
                }
            } else {
                bignum = intLimit.Bignum.Abs();
                isFixnum = intLimit.Bignum.AsInt32(out fixnum);
            }

            if (isFixnum) {
                if (fixnum == 0) {
                    return generator.NextDouble();
                } else {
                    return ScriptingRuntimeHelpers.Int32ToObject(generator.Next(fixnum));
                }
            } else {
                return generator.Random(bignum);
            }
        }

        #endregion

        #region set_trace_func, trace_var, untrace_var

        /// <summary>
        /// set_trace_func(proc): replaces the hook of every earlier call with one that is called with
        /// (event, file, line, id, binding, klass) for each of MRI's RUBY_EVENT_ALL events on any
        /// thread. It is a TracePoint underneath, as MRI's is.
        /// </summary>
        [RubyMethod("set_trace_func", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("set_trace_func", RubyMethodAttributes.PublicSingleton)]
        public static Proc SetTraceFunc(RubyContext/*!*/ context, object self, object traceFunc) {
            Proc proc = CastTraceFunc(traceFunc);
            TracePoint.SetTraceFunc(context, proc, null);
            return proc;
        }

        internal static Proc CastTraceFunc(object traceFunc) {
            var proc = traceFunc as Proc;
            if (traceFunc != null && proc == null) {
                throw RubyExceptions.CreateTypeError("trace_func needs to be Proc");
            }
            return proc;
        }

        //trace_var
        //untrace_var

        #endregion

        #region to_enum

        [RubyMethod("to_enum")]
        [RubyMethod("enum_for")]
        [ReceivesKeywords]
        public static Enumerator/*!*/ Create(object self, [DefaultProtocol, NotNull]string/*!*/ enumeratorName, params object[]/*!*/ targetParameters) {
            return new Enumerator(self, enumeratorName, targetParameters);
        }

        #endregion
    }
}

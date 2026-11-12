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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using IronRuby.Runtime;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Builtins {

    [RubyClass("Numeric", Inherits = typeof(object)), Includes(typeof(Comparable))]
    public class Numeric : RubyObject {

        public Numeric(RubyClass/*!*/ cls) 
            : base(cls) { 
        }

        protected Numeric(SerializationInfo/*!*/ info, StreamingContext context) 
            : base(info, context) {
        }

        #region +@, -@

        /// <summary>
        /// Unary plus - returns the receivers value
        /// </summary>
        [RubyMethod("+@")]
        public static object UnaryPlus(object self) {
            return self;
        }

        /// <summary>
        /// Unary minus - returns the receivers value, negated.
        /// </summary>
        /// <remarks>
        /// Equivalent to:
        /// <code>
        ///     c = 0.coerce(self);
        ///     c[0] - c[1]
        /// </code>
        /// </remarks>
        [RubyMethod("-@")]
        public static object UnaryMinus(BinaryOpStorage/*!*/ coercionStorage, BinaryOpStorage/*!*/ binaryOpSite, object self) {

            return Protocols.CoerceAndApply(coercionStorage, binaryOpSite, "-", 0, self);
        }

        #endregion

        #region <=>

        /// <summary>
        /// Returns zero if self equals other (and is same type), nil otherwise. 
        /// </summary>
        [RubyMethod("<=>")]
        public static object Compare(object self, object other) {
            if (self == other) {
                return 0;
            }
            return null;
        }

        #endregion

        #region abs

        /// <summary>
        /// Returns the absolute value of self
        /// </summary>
        /// <remarks>
        /// Dynamically invokes &lt; operator on self and 0
        /// If this is true then invokes @- on self.
        /// Otherwise just returns self
        /// </remarks>
        [RubyMethod("abs")]
        public static object Abs(BinaryOpStorage/*!*/ lessThanStorage, UnaryOpStorage/*!*/ minusStorage, object self) {
            var lessThan = lessThanStorage.GetCallSite("<");
            if (RubyOps.IsTrue(lessThan.Target(lessThan, self, 0))) {
                var minus = minusStorage.GetCallSite("-@");
                return minus.Target(minus, self);
            }
            return self;
        }

        #endregion

        #region coerce

        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(int self, int other) {
            return RubyOps.MakeArray2(other, self);
        }

        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(double self, double other) {
            return RubyOps.MakeArray2(other, self);
        }

        /// <summary>
        /// If other is the same type as self, returns an array [other, self].
        /// Otherwise, returns an array [floatOther, floatSelf], whose elements are other and self represented as Float objects.
        /// </summary>
        /// <remarks>
        /// This coercion mechanism is used by Ruby to handle mixed-type numeric operations:
        /// It is intended to find a compatible common type between the two operands of the operator. 
        /// </remarks>
        [RubyMethod("coerce")]
        public static RubyArray/*!*/ Coerce(ConversionStorage<double>/*!*/ tof1, ConversionStorage<double>/*!*/ tof2, 
            object self, object other) {

            var context = tof1.Context;

            if (context.GetClassOf(self) == context.GetClassOf(other)) {
                return RubyOps.MakeArray2(other, self);
            }

            var site1 = tof1.GetSite(ConvertToFAction.Make(context));
            var site2 = tof2.GetSite(ConvertToFAction.Make(context));
            return RubyOps.MakeArray2(site1.Target(site1, other), site2.Target(site2, self));
        }

        #endregion

        #region div

        /// <summary>
        /// Dynamically invokes / operator to perform division, then converts the result to an integer.
        /// </summary>
        /// <remarks>
        /// Numeric does not define the / operator; this is left to subclasses.
        /// </remarks>
        [RubyMethod("div")]
        public static object Div(BinaryOpStorage/*!*/ divideStorage, ConversionStorage<double>/*!*/ tofStorage, object self, object other) {
            var divide = divideStorage.GetCallSite("/");
            var tof = tofStorage.GetSite(ConvertToFAction.Make(tofStorage.Context));
            return ClrFloat.Floor(tof.Target(tof, divide.Target(divide, self, other)));
        }

        #endregion

        #region divmod

        /// <summary>
        /// Returns an array [quotient, modulus] obtained by dividing self by other.
        /// The quotient is rounded toward -infinity.
        /// If q, r = x.divmod(y), then 
        ///     q = floor(float(x)/float(y))
        ///     x = q*y + r
        /// </summary>
        /// <remarks>
        /// The quotient is found by directly calling Numeric#div
        /// The modulus is found by dynamically invoking modulo method on self passing other.
        /// </remarks>
        [RubyMethod("divmod")]
        public static RubyArray DivMod(BinaryOpStorage/*!*/ divideStorage, BinaryOpStorage/*!*/ moduloStorage, ConversionStorage<double>/*!*/ tofStorage,
            object self, object other) {

            object div = Div(divideStorage, tofStorage, self, other);

            var modulo = moduloStorage.GetCallSite("modulo");
            object mod = modulo.Target(modulo, self, other);

            return RubyOps.MakeArray2(div, mod);
        }

        #endregion

        #region eql?

        /// <summary>
        /// Returns true if num and numeric are the same type and have equal values. 
        /// </summary>
        [RubyMethod("eql?")]
        public static bool Eql(BinaryOpStorage/*!*/ equals, object self, object other) {
            if (equals.Context.GetClassOf(self) != equals.Context.GetClassOf(other)) {
                return false;
            }
            return Protocols.IsEqual(equals, self, other);
        }

        #endregion

        #region round, floor, ceil, truncate

        /// <summary>
        /// Rounds self to the nearest integer.
        /// </summary>
        /// <remarks>
        /// This is achieved by converting self to a Float and directly calling Float#round.
        /// </remarks>
        [RubyMethod("round")]
        public static object Round([DefaultProtocol]double self) {
            return ClrFloat.Round(self);
        }

        /// <summary>
        /// Returns the largest integer less than or equal to self.
        /// </summary>
        /// <remarks>
        /// This is achieved by converting self to a Float and directly calling Float#floor. 
        /// </remarks>
        [RubyMethod("floor")]
        public static object Floor([DefaultProtocol]double self) {
            return ClrFloat.Floor(self);
        }

        /// <summary>
        /// Returns the smallest Integer greater than or equal to num.
        /// </summary>
        /// <remarks>
        /// This is achieved by converting self to a Float then directly calling Float#ceil.
        /// </remarks>
        [RubyMethod("ceil")]
        public static object Ceil([DefaultProtocol]double self) {
            return ClrFloat.Ceil(self);
        }

        /// <summary>
        /// Returns self truncated to an integer.
        /// </summary>
        /// <remarks>
        /// This is achieved by converting self to a float and directly calling Float#truncate. 
        /// </remarks>
        [RubyMethod("truncate")]
        public static object Truncate([DefaultProtocol]double self) {
            return ClrFloat.ToInt(self);
        }

        // With a number of digits (and for #round, half:) too, as Float's: MRI's are flo_round & co. on self.to_f.
        [RubyMethod("round")]
        public static object Round(ConversionStorage<IntegerValue>/*!*/ integerCast, [DefaultProtocol]double self,
            object ndigits, [Keywords, Optional]object options) {
            return ClrFloat.Round(integerCast, self, ndigits, options);
        }

        [RubyMethod("floor")]
        public static object Floor(ConversionStorage<IntegerValue>/*!*/ integerCast, [DefaultProtocol]double self, object ndigits) {
            return ClrFloat.Floor(integerCast, self, ndigits);
        }

        [RubyMethod("ceil")]
        public static object Ceil(ConversionStorage<IntegerValue>/*!*/ integerCast, [DefaultProtocol]double self, object ndigits) {
            return ClrFloat.Ceil(integerCast, self, ndigits);
        }

        [RubyMethod("truncate")]
        public static object Truncate(ConversionStorage<IntegerValue>/*!*/ integerCast, [DefaultProtocol]double self, object ndigits) {
            return ClrFloat.Truncate(integerCast, self, ndigits);
        }

        #endregion

        #region integer?
        /// <summary>
        /// Returns true if self is an Integer (i.e. Fixnum or Bignum).
        /// </summary>
        [RubyMethod("integer?")]
        public static bool IsInteger(object self) {
            return false;
        }
        #endregion

        #region modulo

        /// <summary>
        /// Equivalent to self.divmod(other)[1]
        /// </summary>
        /// <remarks>
        /// This is achieved by dynamically invoking % operator on self and other.
        /// </remarks>
        [RubyMethod("modulo")]
        public static object Modulo(BinaryOpStorage/*!*/ moduloStorage, object self, object other) {

            var modulo = moduloStorage.GetCallSite("%");
            return modulo.Target(modulo, self, other);
        }

        #endregion

        #region nonzero?

        /// <summary>
        /// Returns num if num is not zero, nil otherwise.
        /// </summary>
        /// <example>
        /// This behavior is useful when chaining comparisons: 
        ///     a = %w( z Bb bB bb BB a aA Aa AA A )
        ///     b = a.sort {|a,b| (a.downcase &lt;=&gt; b.downcase).nonzero? || a &lt;=&gt; b }
        ///     b   #=> ["A", "a", "AA", "Aa", "aA", "BB", "Bb", "bB", "bb", "z"]
        /// </example>
        /// <remarks>
        /// This is achieved by dynamically invoking IsZero on self;
        /// returning nil if it is or self otherwise.
        /// </remarks>
        [RubyMethod("nonzero?")]
        public static object IsNonZero(UnaryOpStorage/*!*/ isZeroStorage, object self) {
            var isZero = isZeroStorage.GetCallSite("zero?");
            return Protocols.IsTrue(isZero.Target(isZero, self)) ? null : self;
        }

        #endregion

        #region remainder

        /// <summary>
        /// If self and other have different signs, returns (self.modulo(other)-other;
        /// otherwise, returns self.modulo(other).
        /// </summary>
        /// <remarks>
        /// This is achieved by dynamically invoking modulo on self;
        /// then invoking &lt; operator on the self and other against 0.
        /// </remarks>
        [RubyMethod("remainder")]
        public static object Remainder(
            BinaryOpStorage/*!*/ equals, 
            BinaryOpStorage/*!*/ greaterThanStorage,
            BinaryOpStorage/*!*/ lessThanStorage,
            BinaryOpStorage/*!*/ minusStorage,
            BinaryOpStorage/*!*/ moduloStorage,
            object self, object other) {

            var modulo = moduloStorage.GetCallSite("%");
            object remainder = modulo.Target(modulo, self, other);

            if (!Protocols.IsEqual(equals, remainder, 0)) {
                var greaterThan = greaterThanStorage.GetCallSite(">");
                var lessThan = lessThanStorage.GetCallSite("<");

                // modulo is not zero
                if (RubyOps.IsTrue(lessThan.Target(lessThan, self, 0)) && RubyOps.IsTrue(greaterThan.Target(greaterThan, other, 0)) ||
                    RubyOps.IsTrue(greaterThan.Target(greaterThan, self, 0)) && RubyOps.IsTrue(lessThan.Target(lessThan, other, 0))) {
                    // (self is negative and other is positive) OR (self is positive and other is negative)
                    var minus = minusStorage.GetCallSite("-");
                    return minus.Target(minus, remainder, other);
                }
            }
            // Either modulo is zero or self and other are not of the same sign
            return remainder;
        }

        #endregion

        #region step

        /// <summary>
        /// Numeric#step (MRI's num_step). With a block and Fixnum or Float arguments of which one is a
        /// Float, MRI's ruby_float_step loop runs here (<see cref="Integer.TryFloatStep"/>); everything
        /// else is the prelude's (__ir_step_slow__ in ruby4.rb), whose `def step(*args, **kw)` this
        /// replaces: getting into it cost ~6 us, more than a short Float step's loop. Integer has its
        /// own #step for Fixnums (Integer.cs).
        /// </summary>
        [RubyMethod("step")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        [ReceivesKeywords]
        public static object Step(CallSiteStorage<Func<CallSite, object, Proc, object, object, object>>/*!*/ slow,
            BlockParam block, object self, params object[]/*!*/ args) {
            object result;
            if (block != null && Integer.TryFloatStep(block, self, args, out result)) {
                return result;
            }
            return Integer.StepSlow(slow, block, self, args);
        }

        #endregion

        #region to_int
        
        /// <summary>
        /// Invokes the self.to_i method to convert self to an integer.
        /// </summary>
        [RubyMethod("to_int")]
        public static object ToInt(UnaryOpStorage/*!*/ toiStorage, object self) {
            var site = toiStorage.GetCallSite("to_i");
            return site.Target(site, self);
        }
        
        #endregion

        #region zero?
        
        /// <summary>
        /// Returns true if self has a zero value. 
        /// </summary>
        [RubyMethod("zero?")]
        public static bool IsZero(BinaryOpStorage/*!*/ equals, object self) {
            return Protocols.IsEqual(equals, self, 0);
        }
        
        #endregion
    }
}

/* ****************************************************************************
 *
 * Rational: its representation. The methods are rational.rb's (Src/StdLib/ironruby/
 * ironruby/rational.rb, a port of MRI 4.0's rational.c); what is here is what that
 * file builds on - the two Integers a Rational is made of, read and made without a
 * Ruby method call or an instance variable lookup.
 *
 * ***************************************************************************/

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// A Rational: two Integers, the denominator positive, frozen from birth (MRI's struct RRational).
    /// They were @numerator and @denominator of a plain Numeric; fields make one ~10x cheaper to make
    /// and to read, and leave #instance_variables empty, as in MRI.
    /// </summary>
    [RubyClass("Rational", Inherits = typeof(Numeric))]
    public class RubyRational : Numeric {
        // null until the parts are set: an instance Marshal allocates before #marshal_load runs
        private object _numerator;
        private object _denominator;

        public RubyRational(RubyClass/*!*/ cls)
            : base(cls) {
        }

        public RubyRational(RubyClass/*!*/ cls, object numerator, object denominator)
            : base(cls) {
            _numerator = numerator;
            _denominator = denominator;
            Freeze();
        }

        public object Numerator { get { return _numerator; } }
        public object Denominator { get { return _denominator; } }

        #region the methods rational.rb builds on

        [RubyMethod("numerator")]
        public static object GetNumerator(RubyRational/*!*/ self) {
            return self._numerator;
        }

        [RubyMethod("denominator")]
        public static object GetDenominator(RubyRational/*!*/ self) {
            return self._denominator;
        }

        /// <summary>
        /// MRI's nurat_s_new_internal: a frozen Rational of exactly these parts, no checks, no
        /// canonicalisation - the callers have done what they need. rational.rb's constructor.
        /// </summary>
        [RubyMethod("__new_internal__", RubyMethodAttributes.PublicSingleton)]
        public static RubyRational/*!*/ NewInternal(RubyClass/*!*/ self, object numerator, object denominator) {
            return new RubyRational(self, numerator, denominator);
        }

        /// <summary>
        /// The parts of an instance that has none yet - the one Marshal allocates for #marshal_load -
        /// which is then frozen. Anything else is frozen already and refuses, as MRI's does.
        /// </summary>
        [RubyMethod("__ir_set_parts__", RubyMethodAttributes.PrivateInstance)]
        public static RubyRational/*!*/ SetParts(RubyContext/*!*/ context, RubyRational/*!*/ self, object numerator, object denominator) {
            if (self.IsFrozen) {
                throw RubyExceptions.CreateObjectFrozenError(context, self);
            }
            self._numerator = numerator;
            self._denominator = denominator;
            self.Freeze();
            return self;
        }

        #endregion

        #region arithmetic (rational.c's rb_rational_plus, _minus, _mul, _div, f_addsub, f_muldiv)

        // An Integer as the CLR holds one: an Int32, an Int64 past it, a BigInteger past that.
        internal static bool IsIntegerValue(object value) {
            return value is int || value is long || value is BigInteger;
        }

        private static BigInteger Big(object value) {
            if (value is int) { return (int)value; }
            if (value is long) { return (long)value; }
            return (BigInteger)value;
        }

        // the value of an Int32 or Int64 Integer
        private static bool TryLong(object value, out long result) {
            if (value is int) { result = (int)value; return true; }
            if (value is long) { result = (long)value; return true; }
            result = 0;
            return false;
        }

        // small enough that the products and sums below stay inside a long
        private static bool Small(object value, out long result) {
            return TryLong(value, out result) && result >= Int32.MinValue && result <= Int32.MaxValue;
        }

        private static long Gcd(long a, long b) {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0) {
                long t = a % b;
                a = b;
                b = t;
            }
            return a;
        }

        private RubyClass/*!*/ RationalClass {
            get { return ImmediateClass.GetNonSingletonClass(); }
        }

        // nurat_s_canonicalize_internal_no_reduce
        private RubyRational/*!*/ NoReduce(long num, long den) {
            if (den < 0) {
                num = -num;
                den = -den;
            } else if (den == 0) {
                throw new DivideByZeroException("divided by 0");
            }
            return new RubyRational(RationalClass, Protocols.Normalize(num), Protocols.Normalize(den));
        }

        private RubyRational/*!*/ NoReduce(BigInteger num, BigInteger den) {
            if (den.Sign < 0) {
                num = -num;
                den = -den;
            } else if (den.IsZero) {
                throw new DivideByZeroException("divided by 0");
            }
            return new RubyRational(RationalClass, Protocols.Normalize(num), Protocols.Normalize(den));
        }

        // f_addsub
        private RubyRational/*!*/ AddSub(object anum, object aden, object bnum, object bden, bool plus) {
            long an, ad, bn, bd;
            if (Small(anum, out an) && Small(aden, out ad) && Small(bnum, out bn) && Small(bden, out bd)) {
                long g = Gcd(ad, bd);
                long a = an * (bd / g);
                long b = bn * (ad / g);
                long c = plus ? a + b : a - b;
                long b2 = ad / g;
                long g2 = Gcd(c, g);
                long num = c / g2;
                long den = (bd / g2) * b2;
                return NoReduce(num, den);
            }
            BigInteger An = Big(anum), Ad = Big(aden), Bn = Big(bnum), Bd = Big(bden);
            BigInteger G = BigInteger.GreatestCommonDivisor(Ad, Bd);
            BigInteger A = An * (Bd / G);
            BigInteger B = Bn * (Ad / G);
            BigInteger C = plus ? A + B : A - B;
            BigInteger B2 = Ad / G;
            BigInteger G2 = BigInteger.GreatestCommonDivisor(C, G);
            return NoReduce(C / G2, (Bd / G2) * B2);
        }

        // f_muldiv, the division already turned into a multiplication by the reciprocal
        private RubyRational/*!*/ MulDiv(object anum, object aden, object bnum, object bden, bool divide) {
            long an, ad, bn, bd;
            if (Small(anum, out an) && Small(aden, out ad) && Small(bnum, out bn) && Small(bden, out bd)) {
                if (divide) {
                    if (bn < 0) {
                        an = -an;
                        bn = -bn;
                    }
                    long t = bn; bn = bd; bd = t;
                }
                long g1 = Gcd(an, bd);
                long g2 = Gcd(ad, bn);
                if (g1 == 0) { g1 = 1; }
                if (g2 == 0) { g2 = 1; }
                return NoReduce((an / g1) * (bn / g2), (ad / g2) * (bd / g1));
            }
            BigInteger An = Big(anum), Ad = Big(aden), Bn = Big(bnum), Bd = Big(bden);
            if (divide) {
                if (Bn.Sign < 0) {
                    An = -An;
                    Bn = -Bn;
                }
                BigInteger t = Bn; Bn = Bd; Bd = t;
            }
            BigInteger G1 = BigInteger.GreatestCommonDivisor(An, Bd);
            BigInteger G2 = BigInteger.GreatestCommonDivisor(Ad, Bn);
            if (G1.IsZero) { G1 = BigInteger.One; }
            if (G2.IsZero) { G2 = BigInteger.One; }
            return NoReduce((An / G1) * (Bn / G2), (Ad / G2) * (Bd / G1));
        }

        // Anything that is not an Integer, a Float or a Rational: rb_num_coerce_bin, in rational.rb.
        private static object CoerceBin(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ coerce, RubyRational/*!*/ self,
            object other, string/*!*/ op) {
            var site = coerce.GetCallSite("__ir_num_coerce_bin__", 2);
            return site.Target(site, self, other, coerce.Context.CreateAsciiSymbol(op));
        }

        // A Rational operand: the commonest case, as an overload of its own so that the binder can
        // pick it by the operand's type, without the storage the coercion of anything else needs.
        [RubyMethod("+")]
        public static object Add(RubyRational/*!*/ self, [NotNull]RubyRational/*!*/ other) {
            return self.AddSub(self._numerator, self._denominator, other._numerator, other._denominator, true);
        }

        [RubyMethod("+")]
        public static object Add(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ coerce, RubyRational/*!*/ self, object other) {
            if (IsIntegerValue(other)) {
                return self.AddSub(self._numerator, self._denominator, other, 1, true);
            }
            if (other is double) {
                return ToFloat(self) + (double)other;
            }
            var r = other as RubyRational;
            if (r != null) {
                return self.AddSub(self._numerator, self._denominator, r._numerator, r._denominator, true);
            }
            return CoerceBin(coerce, self, other, "+");
        }

        [RubyMethod("-")]
        public static object Subtract(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ coerce, RubyRational/*!*/ self, object other) {
            if (IsIntegerValue(other)) {
                return self.AddSub(self._numerator, self._denominator, other, 1, false);
            }
            if (other is double) {
                return ToFloat(self) - (double)other;
            }
            var r = other as RubyRational;
            if (r != null) {
                return self.AddSub(self._numerator, self._denominator, r._numerator, r._denominator, false);
            }
            return CoerceBin(coerce, self, other, "-");
        }

        [RubyMethod("*")]
        public static object Multiply(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ coerce, RubyRational/*!*/ self, object other) {
            if (IsIntegerValue(other)) {
                return self.MulDiv(self._numerator, self._denominator, other, 1, false);
            }
            if (other is double) {
                return ToFloat(self) * (double)other;
            }
            var r = other as RubyRational;
            if (r != null) {
                return self.MulDiv(self._numerator, self._denominator, r._numerator, r._denominator, false);
            }
            return CoerceBin(coerce, self, other, "*");
        }

        [RubyMethod("/"), RubyMethod("quo")]
        public static object Divide(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ coerce, RubyRational/*!*/ self, object other) {
            if (IsIntegerValue(other)) {
                if (Big(other).IsZero) {
                    throw new DivideByZeroException("divided by 0");
                }
                return self.MulDiv(self._numerator, self._denominator, other, 1, true);
            }
            if (other is double) {
                return ToFloat(self) / (double)other;
            }
            var r = other as RubyRational;
            if (r != null) {
                if (Big(r._numerator).IsZero) {
                    throw new DivideByZeroException("divided by 0");
                }
                if (IsOne(self._numerator) && IsOne(self._denominator)) {
                    return self.NoReduce(Big(r._denominator), Big(r._numerator));
                }
                return self.MulDiv(self._numerator, self._denominator, r._numerator, r._denominator, true);
            }
            return CoerceBin(coerce, self, other, "/");
        }

        private static bool IsOne(object value) {
            return value is int && (int)value == 1;
        }

        #endregion

        #region canonical construction (nurat_s_canonicalize_internal)

        /// <summary>
        /// num/den in lowest terms with the sign on the numerator; ZeroDivisionError for a zero
        /// denominator. Both must be Integers (Int32, Int64 or BigInteger).
        /// </summary>
        internal static RubyRational/*!*/ Canonicalize(RubyClass/*!*/ cls, object num, object den) {
            long n, d;
            if (Small(num, out n) && Small(den, out d)) {
                if (d < 0) {
                    n = -n;
                    d = -d;
                } else if (d == 0) {
                    throw new DivideByZeroException("divided by 0");
                }
                if (n != 1 && d != 1) {
                    long g = Gcd(n, d);
                    if (g > 1) {
                        n /= g;
                        d /= g;
                    }
                }
                return new RubyRational(cls, Protocols.Normalize(n), Protocols.Normalize(d));
            }
            BigInteger N = Big(num), D = Big(den);
            if (D.Sign < 0) {
                N = -N;
                D = -D;
            } else if (D.IsZero) {
                throw new DivideByZeroException("divided by 0");
            }
            if (!N.IsOne && !D.IsOne) {
                BigInteger G = BigInteger.GreatestCommonDivisor(N, D);
                if (G > BigInteger.One) {
                    N /= G;
                    D /= G;
                }
            }
            return new RubyRational(cls, Protocols.Normalize(N), Protocols.Normalize(D));
        }

        #endregion

        #region <=>, ==, to_f, hash

        // rb_dbl_cmp
        private static object DoubleCompare(double a, double b) {
            if (Double.IsNaN(a) || Double.IsNaN(b)) { return null; }
            return ScriptingRuntimeHelpers.Int32ToObject(a == b ? 0 : a > b ? 1 : -1);
        }

        // rb_rational_cmp
        [RubyMethod("<=>")]
        public static object Compare(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ coerce, RubyRational/*!*/ self, object other) {
            object bnum, bden;
            if (IsIntegerValue(other)) {
                bnum = other;
                bden = 1;
            } else if (other is RubyRational) {
                bnum = ((RubyRational)other)._numerator;
                bden = ((RubyRational)other)._denominator;
            } else if (other is double) {
                return DoubleCompare(ToFloat(self), (double)other);
            } else {
                var site = coerce.GetCallSite("__ir_num_coerce_cmp__", 2);
                return site.Target(site, self, other, coerce.Context.CreateAsciiSymbol("<=>"));
            }
            long an, ad, bn, bd;
            int sign;
            if (Small(self._numerator, out an) && Small(self._denominator, out ad) && Small(bnum, out bn) && Small(bden, out bd)) {
                sign = Math.Sign(an * bd - bn * ad);
            } else {
                sign = (Big(self._numerator) * Big(bden) - Big(bnum) * Big(self._denominator)).Sign;
            }
            return ScriptingRuntimeHelpers.Int32ToObject(sign);
        }

        // nurat_eqeq_p
        [RubyMethod("==")]
        public static bool Equal(BinaryOpStorage/*!*/ equals, RubyRational/*!*/ self, object other) {
            if (IsIntegerValue(other)) {
                if (Big(self._numerator).IsZero && Big(other).IsZero) { return true; }
                return IsOne(self._denominator) && Big(self._numerator) == Big(other);
            }
            if (other is double) {
                object c = DoubleCompare(ToFloat(self), (double)other);
                return c is int && (int)c == 0;
            }
            var r = other as RubyRational;
            if (r != null) {
                if (Big(self._numerator).IsZero && Big(r._numerator).IsZero) { return true; }
                return Big(self._numerator) == Big(r._numerator) && Big(self._denominator) == Big(r._denominator);
            }
            var site = equals.GetCallSite("==");
            return RubyOps.IsTrue(site.Target(site, other, self));
        }

        // nurat_to_double: the exact quotient, rounded once. Two doubles that hold their Integers
        // exactly divide that way; anything larger is Integer#fdiv's (rational.rb's #to_f before).
        private static readonly long MaxExactDouble = 1L << 53;

        public static double ToFloat(RubyRational/*!*/ self) {
            long n, d;
            if (TryLong(self._numerator, out n) && TryLong(self._denominator, out d) &&
                n > -MaxExactDouble && n < MaxExactDouble && d < MaxExactDouble) {
                return (double)n / (double)d;
            }
            return BigFloatQuotient(Big(self._numerator), Big(self._denominator));
        }

        [RubyMethod("to_f")]
        public static object ToF(BinaryOpStorage/*!*/ fdiv, RubyRational/*!*/ self) {
            long n, d;
            if (TryLong(self._numerator, out n) && TryLong(self._denominator, out d) &&
                n > -MaxExactDouble && n < MaxExactDouble && d < MaxExactDouble) {
                return (double)n / (double)d;
            }
            var site = fdiv.GetCallSite("fdiv");
            return site.Target(site, self._numerator, self._denominator);
        }

        // what Integer#fdiv answers for two Integers (ClrBigInteger's exact division), for the
        // arithmetic above that wants a double without a call
        private static double BigFloatQuotient(BigInteger n, BigInteger d) {
            return ClrInteger.FDiv(n, d);
        }

        // nurat_hash: a function of the two parts, the same for any representation of them
        [RubyMethod("hash")]
        public static int Hash(RubyRational/*!*/ self) {
            int h1 = Big(self._numerator).GetHashCode();
            int h2 = Big(self._denominator).GetHashCode();
            return unchecked((h1 * 31 + h2) * 31 + 0x52617469);
        }

        #endregion
    }
}

/* ****************************************************************************
 *
 * Complex: its representation. The methods are complex.rb's (Src/StdLib/ironruby/
 * ironruby/complex.rb, a port of MRI 4.0's complex.c), apart from the ones moved here;
 * what is here is what that file builds on - the real and imaginary parts, read and
 * made without a Ruby method call or an instance variable lookup.
 *
 * ***************************************************************************/

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// A Complex: a real and an imaginary part, each a real Numeric, frozen from birth (MRI's struct
    /// RComplex). They were @real and @image of a plain Numeric.
    /// </summary>
    [RubyClass("Complex", Inherits = typeof(Numeric))]
    public class RubyComplex : Numeric {
        // null until the parts are set: an instance Marshal allocates before #marshal_load runs
        private object _real;
        private object _imaginary;

        public RubyComplex(RubyClass/*!*/ cls)
            : base(cls) {
        }

        public RubyComplex(RubyClass/*!*/ cls, object real, object imaginary)
            : base(cls) {
            _real = real;
            _imaginary = imaginary;
            Freeze();
        }

        public object Real { get { return _real; } }
        public object Imaginary { get { return _imaginary; } }

        [RubyMethod("real")]
        public static object GetReal(RubyComplex/*!*/ self) {
            return self._real;
        }

        [RubyMethod("imaginary"), RubyMethod("imag")]
        public static object GetImaginary(RubyComplex/*!*/ self) {
            return self._imaginary;
        }

        /// <summary>MRI's nucomp_s_new_internal: no canonicalisation, no checks; frozen.</summary>
        [RubyMethod("__raw__", RubyMethodAttributes.PublicSingleton)]
        public static RubyComplex/*!*/ Raw(RubyClass/*!*/ self, object real, object imaginary) {
            return new RubyComplex(self, real, imaginary);
        }

        #region arithmetic on Integer and Float parts (complex.c's f_add, f_sub, f_mul, safe_mul, comp_mul)

        // The fast paths take parts and operands that are an Int32-sized Integer or a Float; those
        // are combined in a long (an Integer stays one) or a double. Anything else - a Rational part,
        // a Bignum, another kind of operand - is complex.rb's (the __ir_*_slow__ methods), which
        // these follow step for step: complex.c's helpers skip an exact 0 or 1 operand, and that is
        // what keeps an exact zero from turning 0.0 into 0, which ruby/spec observes.

        private static bool IsSimple(object x) {
            if (x is double) { return true; }
            long v;
            return TryLong(x, out v) && v >= Int32.MinValue && v <= Int32.MaxValue;
        }

        private static bool TryLong(object x, out long v) {
            if (x is int) { v = (int)x; return true; }
            if (x is long) { v = (long)x; return true; }
            v = 0;
            return false;
        }

        // an exact Integer 0 (FIXNUM_ZERO_P)
        private static bool FixZero(object x) {
            long v;
            return TryLong(x, out v) && v == 0;
        }

        private static bool FixOne(object x) {
            long v;
            return TryLong(x, out v) && v == 1;
        }

        // f_zero_p: 0.0 counts
        private static bool Zero(object x) {
            if (x is double) { return (double)x == 0.0; }
            return FixZero(x);
        }

        private static double D(object x) {
            if (x is double) { return (double)x; }
            long v;
            TryLong(x, out v);
            return v;
        }

        private static object Plus(object x, object y) {
            long a, b;
            if (TryLong(x, out a) && TryLong(y, out b)) { return Protocols.Normalize(a + b); }
            return D(x) + D(y);
        }

        private static object Minus(object x, object y) {
            long a, b;
            if (TryLong(x, out a) && TryLong(y, out b)) { return Protocols.Normalize(a - b); }
            return D(x) - D(y);
        }

        private static object Times(object x, object y) {
            long a, b;
            if (TryLong(x, out a) && TryLong(y, out b)) { return Protocols.Normalize(a * b); }
            return D(x) * D(y);
        }

        private static object FAdd(object x, object y) {
            if (!(x is double)) {
                if (FixZero(x)) { return y; }
                if (FixZero(y)) { return x; }
            } else if (FixZero(y)) {
                return x;
            }
            return Plus(x, y);
        }

        private static object FSub(object x, object y) {
            return FixZero(y) ? x : Minus(x, y);
        }

        private static object FMul(object x, object y) {
            if (!(x is double)) {
                if (FixZero(y)) { return ScriptingRuntimeHelpers.Int32ToObject(0); }
                if (FixZero(x) && !(y is double)) { return ScriptingRuntimeHelpers.Int32ToObject(0); }
                if (FixOne(x)) { return y; }
                if (FixOne(y)) { return x; }
            } else if (FixOne(y)) {
                return x;
            }
            return Times(x, y);
        }

        private static bool SignBit(double x) {
            return !Double.IsNaN(x) && (x < 0.0 || (x == 0.0 && Double.IsNegative(x)));
        }

        // safe_mul: a Float multiplied by a zero is reduced to its sign first
        private static object SafeMul(object a, object b, bool az, bool bz) {
            if (!az && bz && a is double && !Double.IsNaN((double)a)) {
                a = SignBit((double)a) ? -1.0 : 1.0;
            }
            if (!bz && az && b is double && !Double.IsNaN((double)b)) {
                b = SignBit((double)b) ? -1.0 : 1.0;
            }
            return FMul(a, b);
        }

        private RubyComplex/*!*/ Make(object real, object imaginary) {
            return new RubyComplex(ImmediateClass.GetNonSingletonClass(), real, imaginary);
        }

        private bool HasSimpleParts {
            get { return IsSimple(_real) && IsSimple(_imaginary); }
        }

        private static object Slow(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slow, string/*!*/ name, RubyComplex/*!*/ self, object other) {
            var site = slow.GetCallSite(name, 1);
            return site.Target(site, self, other);
        }

        [RubyMethod("+")]
        public static object Add(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slow, RubyComplex/*!*/ self, object other) {
            if (self.HasSimpleParts) {
                var c = other as RubyComplex;
                if (c != null) {
                    if (c.HasSimpleParts) {
                        return self.Make(FAdd(self._real, c._real), FAdd(self._imaginary, c._imaginary));
                    }
                } else if (IsSimple(other)) {
                    return self.Make(FAdd(self._real, other), self._imaginary);
                }
            }
            return Slow(slow, "__ir_add_slow__", self, other);
        }

        [RubyMethod("-")]
        public static object Subtract(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slow, RubyComplex/*!*/ self, object other) {
            if (self.HasSimpleParts) {
                var c = other as RubyComplex;
                if (c != null) {
                    if (c.HasSimpleParts) {
                        return self.Make(FSub(self._real, c._real), FSub(self._imaginary, c._imaginary));
                    }
                } else if (IsSimple(other)) {
                    return self.Make(FSub(self._real, other), self._imaginary);
                }
            }
            return Slow(slow, "__ir_sub_slow__", self, other);
        }

        [RubyMethod("*")]
        public static object Multiply(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slow, RubyComplex/*!*/ self, object other) {
            if (self.HasSimpleParts) {
                var c = other as RubyComplex;
                if (c != null) {
                    if (c.HasSimpleParts) {
                        // comp_mul
                        object ar = self._real, ai = self._imaginary, br = c._real, bi = c._imaginary;
                        bool arz = Zero(ar), aiz = Zero(ai), brz = Zero(br), biz = Zero(bi);
                        object real = FSub(SafeMul(ar, br, arz, brz), SafeMul(ai, bi, aiz, biz));
                        object imag = FAdd(SafeMul(ar, bi, arz, biz), SafeMul(ai, br, aiz, brz));
                        return self.Make(real, imag);
                    }
                } else if (IsSimple(other)) {
                    return self.Make(FMul(self._real, other), FMul(self._imaginary, other));
                }
            }
            return Slow(slow, "__ir_mul_slow__", self, other);
        }

        // f_divide with f_quo, for Float parts throughout: the quotients are Floats, as they are
        // there (Integer parts make Rationals, which complex.rb makes).
        [RubyMethod("/"), RubyMethod("quo")]
        public static object Divide(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slow, RubyComplex/*!*/ self, object other) {
            if (self._real is double && self._imaginary is double) {
                double ar = (double)self._real, ai = (double)self._imaginary;
                var c = other as RubyComplex;
                if (c != null) {
                    if (c._real is double && c._imaginary is double) {
                        double br = (double)c._real, bi = (double)c._imaginary;
                        double x, y;
                        if (Math.Abs(br) > Math.Abs(bi)) {
                            double r = bi / br;
                            double n = br * (1 + r * r);
                            x = (ar + ai * r) / n;
                            y = (ai - ar * r) / n;
                        } else {
                            double r = br / bi;
                            double n = bi * (1 + r * r);
                            x = (ar * r + ai) / n;
                            y = (ai * r - ar) / n;
                        }
                        return self.Make(x, y);
                    }
                } else if (other is double) {
                    return self.Make(ar / (double)other, ai / (double)other);
                }
            }
            return Slow(slow, "__ir_div_slow__", self, other);
        }

        // nucomp_eqeq_p, with f_eqeq_p: a Float on either side compares as Floats
        private static bool PartEqual(object x, object y) {
            long a, b;
            if (TryLong(x, out a) && TryLong(y, out b)) { return a == b; }
            return D(x) == D(y);
        }

        [RubyMethod("==")]
        public static object Equal(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slow, RubyComplex/*!*/ self, object other) {
            if (self.HasSimpleParts) {
                var c = other as RubyComplex;
                if (c != null) {
                    if (c.HasSimpleParts) {
                        return ScriptingRuntimeHelpers.BooleanToObject(PartEqual(self._real, c._real) && PartEqual(self._imaginary, c._imaginary));
                    }
                } else if (IsSimple(other)) {
                    return ScriptingRuntimeHelpers.BooleanToObject(PartEqual(self._real, other) && Zero(self._imaginary));
                }
            }
            return Slow(slow, "__ir_eqeq_slow__", self, other);
        }

        // nucomp_hash: a function of the parts and their classes (#eql? tells 1 from 1.0)
        [RubyMethod("hash")]
        public static object Hash(CallSiteStorage<Func<CallSite, object, object>>/*!*/ slow, RubyComplex/*!*/ self) {
            if (self.HasSimpleParts) {
                return ScriptingRuntimeHelpers.Int32ToObject(unchecked((PartHash(self._real) * 31 + PartHash(self._imaginary)) * 31 + 0x436f6d70));
            }
            var site = slow.GetCallSite("__ir_hash_slow__", 0);
            return site.Target(site, self);
        }

        private static int PartHash(object x) {
            if (x is double) { return ((double)x).GetHashCode() ^ 0x5a5a5a5a; }
            long v;
            TryLong(x, out v);
            return v.GetHashCode();
        }

        // rb_complex_abs
        [RubyMethod("abs"), RubyMethod("magnitude")]
        public static object Abs(CallSiteStorage<Func<CallSite, object, object>>/*!*/ slow, RubyComplex/*!*/ self) {
            if (self.HasSimpleParts) {
                object re = self._real, im = self._imaginary;
                if (Zero(re)) {
                    object a = AbsPart(im);
                    return (re is double && !(im is double)) ? (object)D(a) : a;
                }
                if (Zero(im)) {
                    object a = AbsPart(re);
                    return (!(re is double) && im is double) ? (object)D(a) : a;
                }
                return RubyMath.HypotDouble(D(re), D(im));
            }
            var site = slow.GetCallSite("__ir_abs_slow__", 0);
            return site.Target(site, self);
        }

        private static object AbsPart(object x) {
            if (x is double) { return Math.Abs((double)x); }
            long v;
            TryLong(x, out v);
            return Protocols.Normalize(Math.Abs(v));
        }

        #endregion

        /// <summary>The parts of the instance Marshal allocates for #marshal_load, which is then frozen.</summary>
        [RubyMethod("__ir_set_parts__", RubyMethodAttributes.PrivateInstance)]
        public static RubyComplex/*!*/ SetParts(RubyContext/*!*/ context, RubyComplex/*!*/ self, object real, object imaginary) {
            if (self.IsFrozen) {
                throw RubyExceptions.CreateObjectFrozenError(context, self);
            }
            self._real = real;
            self._imaginary = imaginary;
            self.Freeze();
            return self;
        }
    }
}

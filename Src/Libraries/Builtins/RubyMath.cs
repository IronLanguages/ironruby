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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using SM = System.Math;

namespace IronRuby.Builtins {

    [RubyModule("Math")]
    public static class RubyMath {

        [RubyConstant]
        public const double E = System.Math.E;

        [RubyConstant]
        public const double PI = System.Math.PI;

        #region Private Implementation Details

        private static double DomainCheck(double result, string/*!*/ functionName) {
            if (double.IsNaN(result)) {
                throw new Errno.DomainError("Domain error - " + functionName);
            }
            return result;
        }

        // The fast path of the functions ruby4.rb used to define in Ruby: a Float or a Fixnum argument,
        // in the function's domain. Anything else - a Bignum, a Rational, nil, a String, an object with
        // #to_f - and every domain error go to the Ruby definitions (__ir_<name>__ in ruby4.rb), which do
        // MRI's rb_to_float conversion and raise its errors.
        private static bool TryGetDouble(object x, out double d) {
            if (x is double) { d = (double)x; return true; }
            if (x is int) { d = (int)x; return true; }
            if (x is long) { d = (long)x; return true; }
            d = 0.0;
            return false;
        }

        private static object General(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, string/*!*/ name, object self, object x) {
            var site = general.GetCallSite(name, new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
            return site.Target(site, self, x);
        }

        private static object General(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ general, string/*!*/ name, object self, object x, object y) {
            var site = general.GetCallSite(name, new RubyCallSignature(2, RubyCallFlags.HasImplicitSelf));
            return site.Target(site, self, x, y);
        }

        #endregion

        #region C library math

        // MRI's Math.gamma, lgamma, erf and erfc are the C library's tgamma, lgamma_r, erf and
        // erfc.  The DLR's MathUtils has series approximations of the four that are good to a few
        // ulps, and a few ulps is a visible difference next to MRI on the same machine:
        // Math.lgamma(3.0) was [0.6931471805599454, 1] here and [0.6931471805599453, 1] there.
        // The C library is one call away on every platform .NET runs on - glibc's or musl's libm,
        // macOS's libSystem, Windows' ucrtbase - so it is used when it can be found, and MathUtils
        // stays as the fallback.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate double UnaryFunction(double x);

        private static readonly IntPtr _cMathLibrary = LoadCMathLibrary();
        private static readonly UnaryFunction _tgamma = GetCMathFunction("tgamma");
        private static readonly UnaryFunction _lgamma = GetCMathFunction("lgamma");
        private static readonly UnaryFunction _erf = GetCMathFunction("erf");
        private static readonly UnaryFunction _erfc = GetCMathFunction("erfc");

        private static IntPtr LoadCMathLibrary() {
            string[] names;
            if (OperatingSystem.IsWindows()) {
                names = new[] { "ucrtbase.dll" };
            } else if (OperatingSystem.IsMacOS()) {
                names = new[] { "/usr/lib/libSystem.dylib", "libSystem.dylib" };
            } else {
                names = new[] { "libm.so.6", "libm.so" };
            }
            foreach (string name in names) {
                try {
                    IntPtr handle;
                    if (NativeLibrary.TryLoad(name, out handle)) {
                        return handle;
                    }
                } catch (Exception) {
                    // an unusual platform: the managed implementations serve
                }
            }
            return IntPtr.Zero;
        }

        private static UnaryFunction GetCMathFunction(string/*!*/ name) {
            if (_cMathLibrary == IntPtr.Zero) {
                return null;
            }
            try {
                IntPtr address;
                if (NativeLibrary.TryGetExport(_cMathLibrary, name, out address)) {
                    return Marshal.GetDelegateForFunctionPointer<UnaryFunction>(address);
                }
            } catch (Exception) {
            }
            return null;
        }

        // 0! .. 22!, each exact as a double, so that gamma(n) is _Factorials[n - 1]. MRI answers
        // Math.gamma of a whole number from the same table rather than from tgamma, whose answer
        // may be off by an ulp; 23! has more significant bits than a double holds, and the table
        // stops where MRI's does.
        private static readonly double[] _Factorials = {
            1.0, 1.0, 2.0, 6.0, 24.0, 120.0, 720.0, 5040.0, 40320.0, 362880.0, 3628800.0,
            39916800.0, 479001600.0, 6227020800.0, 87178291200.0, 1307674368000.0,
            20922789888000.0, 355687428096000.0, 6402373705728000.0, 121645100408832000.0,
            2432902008176640000.0, 51090942171709440000.0, 1124000727777607680000.0
        };

        // MRI, built against glibc, follows cbrt with one Newton step, because glibc's cbrt is
        // not correctly rounded: cbrt(-27.0) is -3.0000000000000004 without it. Built against
        // another C library it takes cbrt as it is. Math.Cbrt is the C library's cbrt.
        private static readonly bool _GlibcCbrtCorrection =
            OperatingSystem.IsLinux() &&
            RuntimeInformation.RuntimeIdentifier.IndexOf("musl", StringComparison.OrdinalIgnoreCase) < 0;

        private static ushort Exponent(byte[] v) {
            return (ushort)((((ushort)(v[7] & 0x7F)) << (ushort)4) | (((ushort)(v[6] & 0xF0)) >> 4));
        }

        private static ulong Mantissa(byte[] v) {
            uint i1 = ((uint)v[0] | ((uint)v[1] << 8) | ((uint)v[2] << 16) | ((uint)v[3] << 24));
            uint i2 = ((uint)v[4] | ((uint)v[5] << 8) | ((uint)(v[6] & 0xF) << 16));

            return (ulong)((ulong)i1 | ((ulong)i2 << 32));
        }
        
        #endregion

        #region Private Instance & Singleton Methods

        [RubyMethodAttribute("acos", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("acos", RubyMethodAttributes.PublicSingleton)]
        public static object Acos(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            if (TryGetDouble(x, out d)) {
                if (double.IsNaN(d)) return d;
                if (d >= -1.0 && d <= 1.0) return SM.Acos(d);
            }
            return General(general, "__ir_acos__", self, x);
        }

        [RubyMethodAttribute("acosh", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("acosh", RubyMethodAttributes.PublicSingleton)]
        public static double Acosh(object self, [DefaultProtocol]double x) {
            //ln(x + sqrt(x*x - 1)) for x >= 1
            return DomainCheck(SM.Log(x + SM.Sqrt(x*x - 1)), "acosh");
        }

        [RubyMethodAttribute("asin", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("asin", RubyMethodAttributes.PublicSingleton)]
        public static object Asin(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            if (TryGetDouble(x, out d)) {
                if (double.IsNaN(d)) return d;
                if (d >= -1.0 && d <= 1.0) return SM.Asin(d);
            }
            return General(general, "__ir_asin__", self, x);
        }

        [RubyMethodAttribute("asinh", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("asinh", RubyMethodAttributes.PublicSingleton)]
        public static object Asinh(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Asinh(d) : General(general, "__ir_asinh__", self, x);
        }

        [RubyMethodAttribute("atan", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("atan", RubyMethodAttributes.PublicSingleton)]
        public static object Atan(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Atan(d) : General(general, "__ir_atan__", self, x);
        }

        [RubyMethodAttribute("atan2", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("atan2", RubyMethodAttributes.PublicSingleton)]
        public static object Atan2(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ general, object self, object y, object x) {
            double dy, dx;
            if (TryGetDouble(y, out dy) && TryGetDouble(x, out dx)) {
                if (double.IsNaN(dy)) return dy;
                if (double.IsNaN(dx)) return dx;
                return SM.Atan2(dy, dx);
            }
            return General(general, "__ir_atan2__", self, y, x);
        }
        
        [RubyMethodAttribute("atanh", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("atanh", RubyMethodAttributes.PublicSingleton)]
        public static double Atanh(object self, [DefaultProtocol]double x) {
            //(1/2) * ln((1+x)/(1-x))
            return DomainCheck(0.5 * SM.Log((1 + x) / (1 - x)), "atanh");
        }
        
        [RubyMethodAttribute("cos", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("cos", RubyMethodAttributes.PublicSingleton)]
        public static object Cos(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Cos(d) : General(general, "__ir_cos__", self, x);
        }

        [RubyMethodAttribute("cosh", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("cosh", RubyMethodAttributes.PublicSingleton)]
        public static object Cosh(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Cosh(d) : General(general, "__ir_cosh__", self, x);
        }

        [RubyMethodAttribute("cbrt", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("cbrt", RubyMethodAttributes.PublicSingleton)]
        public static object CubeRoot(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            if (!TryGetDouble(x, out d)) {
                return General(general, "__ir_cbrt__", self, x);
            }
            double r = SM.Cbrt(d);
            if (_GlibcCbrtCorrection && !Double.IsInfinity(r) && !Double.IsNaN(r) && !(d == 0.0 && r == 0.0)) {
                r = (2.0 * r + (d / r / r)) / 3.0;
            }
            return r;
        }

        [RubyMethodAttribute("erf", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("erf", RubyMethodAttributes.PublicSingleton)]
        public static double Erf(object self, [DefaultProtocol]double x) {
            return _erf != null ? _erf(x) : MathUtils.Erf(x);
        }

        [RubyMethodAttribute("erfc", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("erfc", RubyMethodAttributes.PublicSingleton)]
        public static double Erfc(object self, [DefaultProtocol]double x) {
            return _erfc != null ? _erfc(x) : MathUtils.ErfComplement(x);
        }

        [RubyMethodAttribute("exp", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("exp", RubyMethodAttributes.PublicSingleton)]
        public static object Exp(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Exp(d) : General(general, "__ir_exp__", self, x);
        }

        [RubyMethodAttribute("gamma", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("gamma", RubyMethodAttributes.PublicSingleton)]
        public static double Gamma(object self, [DefaultProtocol]double x) {
            if (x == SM.Floor(x) && x >= 1.0 && x <= _Factorials.Length) {
                return _Factorials[(int)x - 1];
            }
            return DomainCheck(_tgamma != null ? _tgamma(x) : MathUtils.Gamma(x), "gamma");
        }

        [RubyMethodAttribute("hypot", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("hypot", RubyMethodAttributes.PublicSingleton)]
        public static object Hypot(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ general, object self, object x, object y) {
            double dx, dy;
            if (!TryGetDouble(x, out dx) || !TryGetDouble(y, out dy)) {
                return General(general, "__ir_hypot__", self, x, y);
            }
            dx = SM.Abs(dx);
            dy = SM.Abs(dy);
            // an infinity wins over a NaN, which is what C's hypot() promises
            if (double.IsInfinity(dx) || double.IsInfinity(dy)) return double.PositiveInfinity;
            if (double.IsNaN(dx) || double.IsNaN(dy)) return double.NaN;
            if (dx < dy) { double t = dx; dx = dy; dy = t; }
            if (dy == 0.0) return dx;
            // scaling by the larger operand keeps x*x from overflowing for x ~ 1e300
            double r = dy / dx;
            return dx * SM.Sqrt(1.0 + r * r);
        }

        [RubyMethodAttribute("frexp", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("frexp", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Frexp(object self, [DefaultProtocol]double x) {
            byte[] bytes;
            double mantissa;
            int exponent;
            RubyArray result = new RubyArray(2);

            bytes = System.BitConverter.GetBytes(x);
            mantissa = (Mantissa(bytes) * SM.Pow(2, -52) + 1.0) / 2;
            exponent = Exponent(bytes) - 1022;

            result.Add(mantissa);
            result.Add(exponent);
            return result;
        }

        [RubyMethodAttribute("ldexp", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("ldexp", RubyMethodAttributes.PublicSingleton)]
        public static double Ldexp(object self, [DefaultProtocol]double x, [DefaultProtocol]IntegerValue y) {
            return x * SM.Pow(2, y.IsFixnum ? (double)y.Fixnum : Protocols.ToDouble(y.Bignum));
        }

        [RubyMethodAttribute("lgamma", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("lgamma", RubyMethodAttributes.PublicSingleton)]
        public static double LogGamma(object self, [DefaultProtocol]double x) {
            return DomainCheck(_lgamma != null ? _lgamma(x) : MathUtils.LogGamma(x), "lgamma");
        }
        
        // The logarithms of a Float or Fixnum that is not negative, as __ir_log__ & co. in ruby4.rb
        // compute them for one: a NaN comes back as it is, 0.0 and -0.0 give -Infinity. A Fixnum
        // or an Int64 is far below the 2**1024 from which those split the exponent off first.
        // Everything else - a negative number (DomainError), a Bignum, a Rational, nil, a base of
        // nil - goes to the Ruby definitions.
        private static bool TryGetLogArgument(object x, out double d) {
            return TryGetDouble(x, out d) && (d >= 0.0 || double.IsNaN(d));
        }

        [RubyMethodAttribute("log", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("log", RubyMethodAttributes.PublicSingleton)]
        public static object Log(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            if (TryGetLogArgument(x, out d)) {
                return double.IsNaN(d) ? d : SM.Log(d);
            }
            return General(general, "__ir_log__", self, x);
        }

        [RubyMethodAttribute("log", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("log", RubyMethodAttributes.PublicSingleton)]
        public static object Log(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ general, object self, object x, object newBase) {
            double d, b;
            if (TryGetLogArgument(x, out d) && TryGetLogArgument(newBase, out b)) {
                if (double.IsNaN(d)) return d;
                if (double.IsNaN(b)) return b;
                return SM.Log(d) / SM.Log(b);
            }
            return General(general, "__ir_log__", self, x, newBase);
        }

        [RubyMethodAttribute("log10", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("log10", RubyMethodAttributes.PublicSingleton)]
        public static object Log10(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            if (TryGetLogArgument(x, out d)) {
                return double.IsNaN(d) ? d : SM.Log10(d);
            }
            return General(general, "__ir_log10__", self, x);
        }

        [RubyMethodAttribute("log2", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("log2", RubyMethodAttributes.PublicSingleton)]
        public static object Log2(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            if (TryGetLogArgument(x, out d)) {
                return double.IsNaN(d) ? d : SM.Log2(d);
            }
            return General(general, "__ir_log2__", self, x);
        }

        [RubyMethodAttribute("sin", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("sin", RubyMethodAttributes.PublicSingleton)]
        public static object Sin(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Sin(d) : General(general, "__ir_sin__", self, x);
        }

        [RubyMethodAttribute("sinh", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("sinh", RubyMethodAttributes.PublicSingleton)]
        public static object Sinh(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Sinh(d) : General(general, "__ir_sinh__", self, x);
        }

        [RubyMethodAttribute("sqrt", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("sqrt", RubyMethodAttributes.PublicSingleton)]
        public static object Sqrt(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            if (TryGetDouble(x, out d)) {
                if (d > 0.0) return SM.Sqrt(d);
                if (d == 0.0) return 0.0;       // sqrt(-0.0) is -0.0 in IEEE but 0.0 in MRI
                if (double.IsNaN(d)) return d;
            }
            return General(general, "__ir_sqrt__", self, x);
        }

        [RubyMethodAttribute("tan", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("tan", RubyMethodAttributes.PublicSingleton)]
        public static object Tan(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Tan(d) : General(general, "__ir_tan__", self, x);
        }

        [RubyMethodAttribute("tanh", RubyMethodAttributes.PrivateInstance)]
        [RubyMethodAttribute("tanh", RubyMethodAttributes.PublicSingleton)]
        public static object Tanh(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ general, object self, object x) {
            double d;
            return TryGetDouble(x, out d) ? (object)SM.Tanh(d) : General(general, "__ir_tanh__", self, x);
        }

        #endregion
    }
}

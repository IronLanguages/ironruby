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

        // The operands can be products of two Int32-sized parts (comp_mul), so a sum or difference of
        // two of them can pass a long: Complex(-2**31, -2**31) ** 2 has 2**63 for its imaginary part.
        private static object Plus(object x, object y) {
            long a, b;
            if (TryLong(x, out a) && TryLong(y, out b)) {
                long r = unchecked(a + b);
                if (((a ^ r) & (b ^ r)) < 0) { return Protocols.Normalize((BigInteger)a + b); }
                return Protocols.Normalize(r);
            }
            return D(x) + D(y);
        }

        private static object Minus(object x, object y) {
            long a, b;
            if (TryLong(x, out a) && TryLong(y, out b)) {
                long r = unchecked(a - b);
                if (((a ^ b) & (a ^ r)) < 0) { return Protocols.Normalize((BigInteger)a - b); }
                return Protocols.Normalize(r);
            }
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

        #region the string scanner (complex.c's read_comp)

        // What __ir_scan__ found, the second element of its answer; complex.rb's __scan__ makes the value.
        private const int ScanReal = 0;         // x + 0i
        private const int ScanImaginary = 1;    // 0 + xi
        private const int ScanRectangular = 2;  // x + yi
        private const int ScanPolar = 3;        // x @ y

        /// <summary>
        /// complex.c's parse_comp, without making the numbers: [complete?, kind, x, y], where x and y
        /// are the texts str2num is to read (the characters read, underscores taken out), or the
        /// Integers they read as when they are plain decimal integers, or the Integer 1 or -1 that a
        /// bare "i" or "-i" stands for. When no text is left for str2num and the form is not polar,
        /// the answer is [complete?, the Complex] instead. complete? is false when the scan stopped
        /// early, and when <paramref name="strict"/> also when something follows. The scan was
        /// complex.rb's, where it took ~25 us a string, a String and a method call a character.
        /// </summary>
        [RubyMethod("__ir_scan__", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ Scan(RubyClass/*!*/ self, [NotNull]MutableString/*!*/ str, bool strict) {
            RubyArray result = ScanParts(str, strict);
            object x = result[2], y = result[3];
            int kind = (int)result[1];
            if (kind != ScanPolar && !(x is MutableString) && !(y is MutableString)) {
                // f_complex_new2 of real parts is nucomp_s_new_internal
                object zero = ScriptingRuntimeHelpers.Int32ToObject(0);
                result[1] = kind == ScanReal ? new RubyComplex(self, x, zero)
                    : kind == ScanImaginary ? new RubyComplex(self, zero, x)
                    : new RubyComplex(self, x, y);
                result.RemoveRange(2, 2);
            }
            return result;
        }

        private static RubyArray/*!*/ ScanParts(MutableString/*!*/ str, bool strict) {
            Span<char> snapshot = stackalloc char[128];
            ReadOnlySpan<char> text = snapshot;     // (a stack span's scope; it is assigned below)
            if (str.IsBinary) {
                // Held as bytes, which need not be its characters one for one (invalid or multi-byte
                // UTF-8): only ASCII characters take part in the scan, and any other byte is just not
                // one of them. (GetCharacterData is only for a string PrepareForCharacterRead has made
                // characters of, which would raise for an invalid one: MRI's scan does not.)
                byte[] bytes = str.ToByteArray();
                var chars = new char[bytes.Length];
                for (int k = 0; k < bytes.Length; k++) {
                    chars[k] = (char)bytes[k];
                }
                text = chars;
            } else {
                ReadOnlySpan<char> chars;
                ReadOnlySpan<byte> bytes;
                str.GetCharacterData(out chars, out bytes);
                // The scan reads a snapshot: another thread may change the string meanwhile (there is no GVL),
                // and a digit turning into an underscore under ReadDigits would back it up past the start. A
                // short text's snapshot is on the stack.
                if (chars.Length <= snapshot.Length) {
                    chars.CopyTo(snapshot);
                    text = snapshot.Slice(0, chars.Length);
                } else {
                    text = chars.ToArray();
                }
            }
            // What str2num is to read starts in a buffer on the stack (it was a StringBuilder) and grows on the heap
            // only for a number of more than 128 characters.
            var scanner = new ComplexScanner(text, stackalloc char[Math.Min(text.Length, 128)], strict);
            return scanner.Parse();
        }

        private ref struct ComplexScanner {
            private readonly ReadOnlySpan<char> _s;
            private readonly int _n;
            private readonly bool _strict;
            // the characters read so far for str2num, _length of them
            private Span<char> _b;
            private int _length;
            private int _i;

            public ComplexScanner(ReadOnlySpan<char> s, Span<char> buffer, bool strict) {
                _s = s;
                // complex.c works on a C string, so an embedded NUL ends the input
                int nul = s.IndexOf('\0');
                _n = nul >= 0 ? nul : s.Length;
                _strict = strict;
                _b = buffer;
                _length = 0;
                _i = 0;
            }

            private void Append(char c) {
                if (_length == _b.Length) {
                    // each character of the text is read at most once, so the text's length is enough
                    var larger = new char[Math.Max(_length + 1, (int)Math.Min(_s.Length, _b.Length * 2L + 8))];
                    _b.CopyTo(larger);
                    _b = larger;
                }
                _b[_length++] = c;
            }

            // nul ends the input, as it does for the C code
            private char Cur => _i < _n ? _s[_i] : '\0';

            private static bool IsDigit(char c) => c >= '0' && c <= '9';
            private static bool IsSign(char c) => c == '-' || c == '+';
            private static bool IsImaginaryUnit(char c) => c == 'i' || c == 'I' || c == 'j' || c == 'J';
            private static bool IsSpace(char c) => c == ' ' || (c >= '\t' && c <= '\r');

            private char ReadSign() {
                char c = Cur;
                if (IsSign(c)) {
                    Append(c);
                    _i++;
                    return c;
                }
                return '?';
            }

            private bool ReadDigits() {
                if (!IsDigit(Cur)) {
                    return false;
                }
                bool us = true;
                while (IsDigit(Cur) || Cur == '_') {
                    if (Cur == '_') {
                        if (us) {
                            if (_strict) {
                                return false;
                            }
                            break;
                        }
                        us = true;
                    } else {
                        Append(Cur);
                        us = false;
                    }
                    _i++;
                }
                if (us) {
                    // do { s--; } while (*s == '_');
                    do {
                        _i--;
                    } while (_i >= 0 && _s[_i] == '_');
                }
                return true;
            }

            private void Unappend() {
                _length--;
            }

            private bool ReadNum() {
                if (Cur != '.') {
                    if (!ReadDigits()) {
                        return false;
                    }
                }
                if (Cur == '.') {
                    Append('.');
                    _i++;
                    if (!ReadDigits()) {
                        Unappend();
                        return false;
                    }
                }
                if (Cur == 'e' || Cur == 'E') {
                    Append(Cur);
                    _i++;
                    ReadSign();
                    if (!ReadDigits()) {
                        Unappend();
                        return false;
                    }
                }
                return true;
            }

            private bool ReadRationalNoSign() {
                if (!ReadNum()) {
                    return false;
                }
                if (Cur == '/') {
                    Append('/');
                    _i++;
                    if (!ReadDigits()) {
                        Unappend();
                        return false;
                    }
                }
                return true;
            }

            private bool ReadRational() {
                ReadSign();
                return ReadRationalNoSign();
            }

            // The text read since from, for str2num; or, when it is a sign and up to 18 digits (str2num's
            // rb_cstr_to_inum, for which an empty text is 0), the Integer it reads as.
            private object/*!*/ Buffered(int from) {
                int length = _length - from;
                int i = from;
                bool negative = false;
                if (length > 0 && IsSign(_b[i])) {
                    negative = _b[i] == '-';
                    i++;
                }
                if (_length - i <= 18) {
                    long value = 0;
                    for (; i < _length && IsDigit(_b[i]); i++) {
                        value = value * 10 + (_b[i] - '0');
                    }
                    if (i == _length) {
                        return ClrInteger.Narrow(negative ? -value : value);
                    }
                }
                return MutableString.CreateAscii(new string(_b.Slice(from, length)));
            }

            private static RubyArray/*!*/ Result(bool ok, int kind, object x, object y = null) {
                var result = new RubyArray(4);
                result.Add(ok);
                result.Add(ScriptingRuntimeHelpers.Int32ToObject(kind));
                result.Add(x);
                result.Add(y);
                return result;
            }

            private static object UnitFor(char sign) {
                return ScriptingRuntimeHelpers.Int32ToObject(sign == '-' ? -1 : 1);
            }

            private RubyArray/*!*/ ReadComplex() {
                int bb = _length;
                char sign = ReadSign();

                if (IsImaginaryUnit(Cur)) {
                    _i++;
                    return Result(true, ScanImaginary, UnitFor(sign));
                }

                if (!ReadRationalNoSign()) {
                    return Result(false, ScanReal, Buffered(bb));
                }
                object num = Buffered(bb);

                if (IsImaginaryUnit(Cur)) {
                    _i++;
                    return Result(true, ScanImaginary, num);
                }

                if (Cur == '@') {
                    _i++;
                    bb = _length;
                    bool st = ReadRational();
                    if (_length == bb || !IsDigit(_b[_length - 1])) {
                        return Result(false, ScanReal, num);
                    }
                    return Result(st, ScanPolar, num, Buffered(bb));
                }

                if (IsSign(Cur)) {
                    bb = _length;
                    sign = ReadSign();
                    object num2;
                    if (IsImaginaryUnit(Cur)) {
                        num2 = UnitFor(sign);
                    } else {
                        if (!ReadRationalNoSign()) {
                            return Result(false, ScanReal, num);
                        }
                        num2 = Buffered(bb);
                    }
                    if (!IsImaginaryUnit(Cur)) {
                        // read_comp has already made num2 by now and drops it: str2num still runs, and
                        // still raises for a zero denominator ("1+2/0x"), so the text goes along as y
                        return Result(false, ScanReal, num, num2);
                    }
                    _i++;
                    return Result(true, ScanRectangular, num, num2);
                }

                return Result(true, ScanReal, num);
            }

            private void SkipSpace() {
                while (_i < _n && IsSpace(_s[_i])) {
                    _i++;
                }
            }

            public RubyArray/*!*/ Parse() {
                SkipSpace();
                RubyArray result = ReadComplex();
                if ((bool)result[0]) {
                    SkipSpace();
                    if (_strict && _i < _n) {
                        result[0] = false;
                    }
                }
                return result;
            }
        }

        #endregion
    }
}

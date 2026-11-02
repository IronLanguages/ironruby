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
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {

    // Ruby 2.4 unified Fixnum and Bignum into a single Integer and 3.2 removed both
    // constants, so this one class backs both CLR representations of an integer: it extends
    // System.Int32, and RubyContext additionally maps System.Numerics.BigInteger onto the
    // very same RubyClass.  The int-self and BigInteger-self operations are therefore
    // overloads of one Ruby method, which is why ClrInteger is a single trait type.
    [RubyClass("Integer", Extends = typeof(int), Inherits = typeof(Numeric))]
    // Precision is a Ruby 1.8 module that MRI removed in 1.9; Integer.ancestors matches
    // CRuby exactly without it.  Float still includes it, so the module itself stays.
    [Includes(typeof(ClrInteger), Copy = true)]
    [UndefineMethod("new", IsStatic = true)]
    public partial class Integer : Numeric {
        public Integer(RubyClass/*!*/ cls) 
            : base(cls) { 
        }

        #region hash

        /// <summary>
        /// Seeded per process, as in MRI (CVE-2011-4815).
        /// </summary>
        [RubyMethod("hash")]
        public static int Hash(int self) {
            return RubyUtils.GetFixnumHashCode(self);
        }

        [RubyMethod("hash")]
        public static int Hash(long self) {
            return RubyUtils.GetIntegerHashCode(self);
        }

        [RubyMethod("hash")]
        public static int Hash([NotNull]BigInteger/*!*/ self) {
            return RubyUtils.GetIntegerHashCode(self);
        }

        #endregion

        #region induced_from

        /// <summary>
        /// Convert obj to an Integer, where obj is Fixnum
        /// </summary>
        /// <remarks>Just returns the Fixnum</remarks>
        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static object InducedFrom(RubyClass/*!*/ self, int obj) {
            return obj;
        }

        /// <summary>
        /// Convert obj to an Integer, where obj is Bignum
        /// </summary>
        /// <remarks>Just returns the Bignum</remarks>
        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static object InducedFrom(RubyClass/*!*/ self, [NotNull]BigInteger/*!*/ obj) {
            return obj;
        }

        /// <summary>
        /// Convert obj to an Integer, where obj is Float
        /// </summary>
        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static object InducedFrom(UnaryOpStorage/*!*/ toiStorage, RubyClass/*!*/ self, double obj) {
            var site = toiStorage.GetCallSite("to_i");
            return site.Target(site, obj);
        }

        /// <summary>
        /// Convert obj to an Integer, where obj is not Fixnum, Bignum or Float
        /// </summary>
        /// <remarks>Just throws an error</remarks>
        /// <exception cref="InvalidOperationException">Assumption is object cannot be induced to Integer</exception>
        [RubyMethod("induced_from", RubyMethodAttributes.PublicSingleton)]
        public static int InducedFrom(RubyClass/*!*/ self, object obj) {
            throw RubyExceptions.CreateTypeError("failed to convert {0} into Integer", self.Context.GetClassDisplayName(obj));
        }

        #endregion

        #region to_i, to_int, floor, ceil, round, truncate

        /// <summary>
        /// As self is already an Integer, just return self.
        /// </summary>
        [RubyMethod("to_i")]
        [RubyMethod("to_int")]
        [RubyMethod("floor")]
        [RubyMethod("ceil")]
        [RubyMethod("round")]
        [RubyMethod("truncate")]
        public static object ToInteger(object/*!*/ self) {
            return self;
        }

        /// <summary>
        /// Rounds self to a multiple of 10**(-ndigits). A non-negative ndigits returns self.
        /// Accepts the <c>half:</c> option (:up, :down or :even).
        /// </summary>
        [RubyMethod("round")]
        public static object Round(ConversionStorage<IntegerValue>/*!*/ integerCast, object/*!*/ self,
            object ndigits, [Optional]object options) {

            var opts = options as IDictionary<object, object>;
            if (opts == null && options == Missing.Value) {
                // round(half: :up) - the only argument is the options hash
                opts = ndigits as IDictionary<object, object>;
                if (opts != null) {
                    ndigits = Missing.Value;
                }
            }

            NumericRounding.Half half = NumericRounding.GetHalfOption(integerCast.Context, opts);
            int nd = (ndigits == Missing.Value) ? 0 : NumericRounding.GetNDigits(integerCast, ndigits);
            if (nd >= 0) {
                return self;
            }
            return NumericRounding.RoundInteger(ToBigInteger(self), nd, half);
        }

        /// <summary>Rounds self down to a multiple of 10**(-ndigits).</summary>
        [RubyMethod("floor")]
        public static object Floor(ConversionStorage<IntegerValue>/*!*/ integerCast, object/*!*/ self, object ndigits) {
            int nd = NumericRounding.GetNDigits(integerCast, ndigits);
            return (nd >= 0) ? self : NumericRounding.FloorInteger(ToBigInteger(self), nd);
        }

        /// <summary>Rounds self up to a multiple of 10**(-ndigits).</summary>
        [RubyMethod("ceil")]
        public static object Ceil(ConversionStorage<IntegerValue>/*!*/ integerCast, object/*!*/ self, object ndigits) {
            int nd = NumericRounding.GetNDigits(integerCast, ndigits);
            return (nd >= 0) ? self : NumericRounding.CeilInteger(ToBigInteger(self), nd);
        }

        /// <summary>Truncates self towards zero to a multiple of 10**(-ndigits).</summary>
        [RubyMethod("truncate")]
        public static object Truncate(ConversionStorage<IntegerValue>/*!*/ integerCast, object/*!*/ self, object ndigits) {
            int nd = NumericRounding.GetNDigits(integerCast, ndigits);
            return (nd >= 0) ? self : NumericRounding.TruncateInteger(ToBigInteger(self), nd);
        }

        private static BigInteger ToBigInteger(object/*!*/ self) {
            if (self is int) {
                return new BigInteger((int)self);
            }
            if (self is long) {
                return new BigInteger((long)self);
            }
            if (self is BigInteger) {
                return (BigInteger)self;
            }
            throw RubyExceptions.CreateTypeError("can't convert {0} into Integer", self == null ? "nil" : self.GetType().Name);
        }

        #endregion

        #region numerator/ord, denominator

        [RubyMethod("numerator")]
        [RubyMethod("ord")]
        public static object/*!*/ Numerator(object/*!*/ self) {
            return self;
        }

        [RubyMethod("denominator")]
        public static object/*!*/ Denominator(object/*!*/ self) {
            return ClrInteger.One;
        }

        #endregion

        #region chr

        /// <summary>
        /// MRI's int_chr. The receiver is first narrowed to an unsigned 32 bit code point;
        /// anything outside 0..0xffffffff is rejected here, and the message depends on whether
        /// the receiver was a Fixnum ("-1 out of char range") or a Bignum ("bignum out of char
        /// range") - which is why the parameter is an <see cref="IntegerValue"/> rather than an
        /// int: taking an int made every oversized receiver fail during argument conversion
        /// with "bignum too big to convert into Fixnum" instead.
        ///
        /// With no encoding argument a code point of 0xff or less is returned as that single
        /// raw byte - US-ASCII below 0x80, BINARY otherwise - no matter what
        /// Encoding.default_internal is. default_internal is only consulted above 0xff, and
        /// when it is nil that case is a RangeError.
        /// </summary>
        [RubyMethod("chr")]
        public static MutableString/*!*/ ToChr(ConversionStorage<MutableString>/*!*/ toStr, [DefaultProtocol]IntegerValue self,
            [Optional]object encoding) {

            BigInteger value = self.IsFixnum ? (BigInteger)self.Fixnum : self.Bignum;
            if (value.Sign < 0 || value > UInt32.MaxValue) {
                if (self.IsFixnum) {
                    throw RubyExceptions.CreateRangeError("{0} out of char range", self.Fixnum);
                }
                throw RubyExceptions.CreateRangeError("bignum out of char range");
            }
            uint codepoint = (uint)value;

            if (encoding == Missing.Value) {
                if (codepoint <= 0xff) {
                    return MutableString.CreateBinary(
                        new[] { (byte)codepoint },
                        codepoint <= 0x7f ? RubyEncoding.Ascii : RubyEncoding.Binary
                    );
                }
                var internalEncoding = toStr.Context.DefaultInternalEncoding;
                if (internalEncoding == null) {
                    throw RubyExceptions.CreateRangeError("{0} out of char range", codepoint);
                }
                return ToChr(internalEncoding, internalEncoding, codepoint);
            }

            var enc = Protocols.ConvertToEncoding(toStr, encoding);
            return ToChr(enc, enc, codepoint);
        }

        internal static MutableString/*!*/ ToChr(RubyEncoding/*!*/ encoding, RubyEncoding/*!*/ resultEncoding, int codepoint) {
            if (codepoint < 0) {
                throw RubyExceptions.CreateRangeError("{0} out of char range", codepoint);
            }
            return ToChr(encoding, resultEncoding, (uint)codepoint);
        }

        /// <summary>MRI's rb_enc_uint_chr: either the encoded character, or a RangeError.</summary>
        private static MutableString/*!*/ ToChr(RubyEncoding/*!*/ encoding, RubyEncoding/*!*/ resultEncoding, uint codepoint) {
            switch (encoding.CodePage) {
                case RubyEncoding.CodePageUTF16BE:
                case RubyEncoding.CodePageUTF16LE:
                    // UTF-16 is the one Unicode form where a too-wide value is reported as invalid
                    // rather than as out of range: a UTF-16 character is never longer than the
                    // surrogate pair it already has room for, so rb_enc_codelen answers
                    // ONIGERR_INVALID_CODE_POINT_VALUE instead of TOO_BIG_WIDE_CHAR_VALUE.
                    if (codepoint > 0x10ffff || codepoint >= 0xd800 && codepoint <= 0xdfff) {
                        throw InvalidCodePoint(encoding, codepoint);
                    }
                    return MutableString.CreateMutable(Tokenizer.UnicodeCodePointToString((int)codepoint), resultEncoding);

                case RubyEncoding.CodePageUTF7:
                case RubyEncoding.CodePageUTF8:
                case RubyEncoding.CodePageUTF32BE:
                case RubyEncoding.CodePageUTF32LE:
                case RubyEncoding.CodePageCESU8:
                    // Lone surrogates are not code points; letting them through produced a
                    // System.Text.EncoderFallbackException out of the encoder later on.
                    if (codepoint > 0x10ffff) {
                        throw OutOfCharRange(codepoint);
                    }
                    if (codepoint >= 0xd800 && codepoint <= 0xdfff) {
                        throw InvalidCodePoint(encoding, codepoint);
                    }
                    return MutableString.CreateMutable(Tokenizer.UnicodeCodePointToString((int)codepoint), resultEncoding);

                case RubyEncoding.CodePageAscii:
                    if (codepoint > 0xff) {
                        throw OutOfCharRange(codepoint);
                    }
                    if (codepoint > 0x7f) {
                        throw InvalidCodePoint(encoding, codepoint);
                    }
                    goto default;

                case RubyEncoding.CodePageSJIS:
                case RubyEncoding.CodePageShiftJIS:
                    // Single byte: ASCII plus the half-width katakana block. Double byte: a
                    // lead byte from 0x81-0x9f/0xe0-0xfc and a trail byte from 0x40-0xfc
                    // excluding 0x7f.
                    if (codepoint <= 0x7f || codepoint >= 0xa1 && codepoint <= 0xdf) {
                        return MutableString.CreateBinary(new[] { (byte)codepoint }, resultEncoding);
                    }
                    if (codepoint <= 0xffff) {
                        uint lead = codepoint >> 8, trail = codepoint & 0xff;
                        if ((lead >= 0x81 && lead <= 0x9f || lead >= 0xe0 && lead <= 0xfc) &&
                            trail >= 0x40 && trail <= 0xfc && trail != 0x7f) {
                            return MutableString.CreateBinary(new[] { (byte)lead, (byte)trail }, resultEncoding);
                        }
                        throw InvalidCodePoint(encoding, codepoint);
                    }
                    throw OutOfCharRange(codepoint);

                case RubyEncoding.CodePageEUCJP:
                case RubyEncoding.CodePageEucJpMs:
                case RubyEncoding.CodePageCP51932:
                    // Single byte ASCII; two byte 0xa1-0xfe pairs for JIS X 0208 and the 0x8e
                    // prefix for half-width katakana; three byte 0x8f pairs for JIS X 0212.
                    if (codepoint <= 0x7f) {
                        return MutableString.CreateBinary(new[] { (byte)codepoint }, resultEncoding);
                    }
                    if (codepoint > 0xff && codepoint <= 0xffff) {
                        uint lead = codepoint >> 8, trail = codepoint & 0xff;
                        if (lead >= 0xa1 && lead <= 0xfe && trail >= 0xa1 && trail <= 0xfe ||
                            lead == 0x8e && trail >= 0xa1 && trail <= 0xdf) {
                            return MutableString.CreateBinary(new[] { (byte)lead, (byte)trail }, resultEncoding);
                        }
                    }
                    if (codepoint > 0xffff && codepoint <= 0xffffff) {
                        uint b1 = codepoint >> 16, b2 = (codepoint >> 8) & 0xff, b3 = codepoint & 0xff;
                        if (b1 == 0x8f && b2 >= 0xa1 && b2 <= 0xfe && b3 >= 0xa1 && b3 <= 0xfe) {
                            return MutableString.CreateBinary(new[] { (byte)b1, (byte)b2, (byte)b3 }, resultEncoding);
                        }
                    }
                    if (codepoint > 0xffffff) {
                        throw OutOfCharRange(codepoint);
                    }
                    throw InvalidCodePoint(encoding, codepoint);

                default:
                    if (codepoint <= 0xff) {
                        return MutableString.CreateBinary(new[] { (byte)codepoint }, resultEncoding);
                    }
                    if (encoding.IsDoubleByteCharacterSet) {
                        if (codepoint <= 0xffff) {
                            return MutableString.CreateBinary(new[] { (byte)(codepoint >> 8), (byte)(codepoint & 0xff) }, resultEncoding);
                        }
                        throw RubyExceptions.CreateRangeError("{0} out of char range", codepoint);
                    }
                    // Everything else - single byte encodings, dummy encodings, and anything whose
                    // structure we don't model - has no character this wide. MRI's rb_enc_uint_chr
                    // reports exactly that rather than refusing to answer: rb_enc_codelen returns
                    // ONIGERR_TOO_BIG_WIDE_CHAR_VALUE and it raises "out of char range".
                    throw OutOfCharRange(codepoint);
            }
        }

        /// <summary>
        /// MRI distinguishes two RangeErrors: a code point that is wider than the encoding's
        /// longest character is "out of char range", while one that fits but is not assigned is
        /// "invalid codepoint". So 0x100.chr("US-ASCII") is "256 out of char range" but
        /// 0x80.chr("US-ASCII") is "invalid codepoint 0x80 in US-ASCII".
        /// </summary>
        private static Exception/*!*/ InvalidCodePoint(RubyEncoding/*!*/ encoding, uint codepoint) {
            return RubyExceptions.CreateRangeError("invalid codepoint 0x{0:X} in {1}", codepoint, encoding.Name);
        }

        private static Exception/*!*/ OutOfCharRange(uint codepoint) {
            return RubyExceptions.CreateRangeError("{0} out of char range", codepoint);
        }

        #endregion

        #region integer?, odd?, even?

        /// <summary>
        /// Always returns true.
        /// </summary>
        /// <returns>true</returns>
        [RubyMethod("integer?")]
        public static new bool IsInteger(object/*!*/ self) {
            return true;
        }

        [RubyMethod("odd?")]
        public static bool IsOdd(int self) {
            return (self & 1) != 0;
        }

        [RubyMethod("odd?")]
        public static bool IsOdd(BigInteger/*!*/ self) {
            return !self.IsEven;
        }

        [RubyMethod("even?")]
        public static bool IsEven(int self) {
            return (self & 1) == 0;
        }

        [RubyMethod("even?")]
        public static bool IsEven(BigInteger/*!*/ self) {
            return self.IsEven;
        }

        /// <summary>
        /// The number of bits of the two's complement representation of self, excluding the
        /// sign bit: 0.bit_length == 0, 255.bit_length == 8, (-256).bit_length == 8.
        /// </summary>
        [RubyMethod("bit_length")]
        public static int BitLength(int self) {
            // ~self for negatives, so that -1 -> 0 and -257 -> 9, the way MRI counts.
            uint magnitude = self < 0 ? (uint)~self : (uint)self;
            return 32 - System.Numerics.BitOperations.LeadingZeroCount(magnitude);
        }

        /// <summary>
        /// <see cref="BigInteger.GetBitLength"/> already uses MRI's definition (shortest
        /// two's complement representation, sign bit excluded).
        /// </summary>
        [RubyMethod("bit_length")]
        public static object BitLength(BigInteger/*!*/ self) {
            long bits = self.GetBitLength();
            return bits <= Int32.MaxValue ? ScriptingRuntimeHelpers.Int32ToObject((int)bits) : (object)(BigInteger)bits;
        }

        #endregion

        #region allbits?, anybits?, nobits?

        /// <summary>
        /// Both operands are widened to <see cref="BigInteger"/> first: BigInteger's bitwise
        /// operators already use the infinite two's complement representation that Ruby
        /// specifies for negative integers, so (~0b1).allbits?(42) comes out true without any
        /// special casing of the sign.
        /// </summary>
        private static BigInteger MaskBits(object/*!*/ self, IntegerValue mask) {
            return ToBigInteger(self) & (mask.IsFixnum ? (BigInteger)mask.Fixnum : mask.Bignum);
        }

        /// <summary>Returns true when every bit set in mask is also set in self.</summary>
        [RubyMethod("allbits?")]
        public static bool AllBits(object/*!*/ self, [DefaultProtocol]IntegerValue mask) {
            BigInteger m = mask.IsFixnum ? (BigInteger)mask.Fixnum : mask.Bignum;
            return MaskBits(self, mask) == m;
        }

        /// <summary>Returns true when at least one bit set in mask is also set in self.</summary>
        [RubyMethod("anybits?")]
        public static bool AnyBits(object/*!*/ self, [DefaultProtocol]IntegerValue mask) {
            return !MaskBits(self, mask).IsZero;
        }

        /// <summary>Returns true when no bit set in mask is set in self.</summary>
        [RubyMethod("nobits?")]
        public static bool NoBits(object/*!*/ self, [DefaultProtocol]IntegerValue mask) {
            return MaskBits(self, mask).IsZero;
        }

        #endregion

        #region ceildiv

        /// <summary>
        /// self.ceildiv(other) == -((-self).div(other)), which is how MRI defines it. Going
        /// through the dynamic `div` (rather than dividing here) is what makes
        /// 3.ceildiv(1.2) and 3.ceildiv(6/5r) work: `div` already floors the result of `/`
        /// for whatever numeric type `other` turns out to be, and always yields an Integer.
        /// </summary>
        [RubyMethod("ceildiv")]
        public static object CeilDiv(UnaryOpStorage/*!*/ negateStorage, BinaryOpStorage/*!*/ divStorage, object/*!*/ self, object other) {
            var negate = negateStorage.GetCallSite("-@");
            var div = divStorage.GetCallSite("div");
            return negate.Target(negate, div.Target(div, negate.Target(negate, self), other));
        }

        #endregion

        #region next, succ, pred

        /// <summary>
        /// Returns the Integer equal to self + 1, where self is Fixnum.
        /// </summary>
        /// <returns>May return Fixnum or Bignum depending on overflow/underflow.</returns>
        /// <example>
        /// 1.next      #=> 2
        /// (-1).next   #=> 0
        /// </example>
        [RubyMethod("succ")]
        [RubyMethod("next")]
        public static object Next(int self) {
            return ClrInteger.Add(self, 1);
        }

        /// <summary>
        /// Returns the Integer equal to self + 1, where self is not Fixnum (probably Bignum).
        /// </summary>
        /// <returns>May return Fixnum or Bignum depending on overflow/underflow.</returns>
        /// <remarks>Dynamically invokes "+" operator to get next value.</remarks>
        [RubyMethod("succ")]
        [RubyMethod("next")]
        public static object Next(BinaryOpStorage/*!*/ addStorage, object/*!*/ self) {
            var site = addStorage.GetCallSite("+");
            return site.Target(site, self, ClrInteger.One);
        }

        [RubyMethod("pred")]
        public static object Pred(int self) {
            return ClrInteger.Subtract(self, 1);
        }

        [RubyMethod("pred")]
        public static object Pred(BinaryOpStorage/*!*/ subStorage, object/*!*/ self) {
            var site = subStorage.GetCallSite("-");
            return site.Target(site, self, ClrInteger.One);
        }

        #endregion

        #region times

        /// <summary>
        /// Iterates block self times, passing in values from zero to self - 1, where self is Fixnum.
        /// </summary>
        /// <returns>self</returns>
        [RubyMethodAttribute("times")]
        public static object Times(BlockParam/*!*/ block, int self) {
            return (block != null) ? TimesImpl(block, self) : new Enumerator(self, "times");
        }

        private static object TimesImpl(BlockParam/*!*/ block, int self) {
            int i = 0;
            while (i < self) {
                object result;
                if (block.Yield(i, out result)) {
                    return result;
                }
                i++;
            }

            return self;
        }

        /// <summary>
        /// Iterates block self times, passing in values from zero to self - 1, where self is not Fixnum (probably Bignum).
        /// </summary>
        /// <returns>self</returns>
        /// <remarks>
        /// Dynamically invokes "+" operator to find next item.
        /// Dynamically invokes "&lt;" operator to check if we have reached self - 1.
        /// </remarks>
        [RubyMethodAttribute("times")]
        public static object Times(BinaryOpStorage/*!*/ lessThanStorage, BinaryOpStorage/*!*/ addStorage, BlockParam block, object/*!*/ self) {
            return (block != null) ? TimesImpl(lessThanStorage, addStorage, block, self) : 
                new Enumerator(self, "times");
        }

        public static object TimesImpl(BinaryOpStorage/*!*/ lessThanStorage, BinaryOpStorage/*!*/ addStorage, BlockParam/*!*/ block, object/*!*/ self) {
            object i = 0;
            var lessThan = lessThanStorage.GetCallSite("<");
            while (RubyOps.IsTrue(lessThan.Target(lessThan, i, self))) {
                object result;
                if (block.Yield(i, out result)) {
                    return result;
                }

                var add = addStorage.GetCallSite("+");
                i = add.Target(add, i, 1);
            }
            return self;
        }

        #endregion

        #region upto

        /// <summary>
        /// Iterates block, passing in integer values from self up to and including other, where both self and other are Fixnum. 
        /// </summary>
        /// <returns>self</returns>
        /// <example>
        ///    5.upto(10) { |i| print i, " " }
        /// produces: 
        ///    5 6 7 8 9 10
        /// </example>
        /// <remarks>
        /// Since both self and other are Fixnum then this algorithm doesn't need to worry about overflowing into Bignum.
        /// </remarks>
        [RubyMethod("upto")]
        public static object UpTo(BlockParam block, int self, int other) {
            return (block != null) ? UpToImpl(block, self, other) : new Enumerator(self, "upto", other);
        }

        private static object UpToImpl(BlockParam block, int self, int other) {
            int i = self;
            while (i <= other) {
                object result;
                if (block.Yield(i, out result)) {
                    return result;
                }
                i++;
            }
            return self;
        }

        /// <summary>
        /// Iterates block, passing in integer values from self up to and including other, where both self and other are Fixnum. 
        /// </summary>
        /// <returns>self</returns>
        /// <remarks>
        /// Dynamically invokes "+" operator to find next item down.
        /// Dynamically invokes "&gt;" operator and takes the negation to see if we have reached the bottom.
        /// This approach automatically deals with Floats and overflow/underflow between Fixnum and Bignum.
        /// </remarks>
        [RubyMethod("upto")]
        public static object UpTo(BinaryOpStorage/*!*/ greaterThanStorage, BinaryOpStorage/*!*/ addStorage, 
            BlockParam block, object/*!*/ self, object other) {
            return (block != null) ? UpToImpl(greaterThanStorage, addStorage, block, self, other) :
                new Enumerator(self, "upto", other);
        }

        private static object UpToImpl(BinaryOpStorage/*!*/ greaterThanStorage, BinaryOpStorage/*!*/ addStorage,
            BlockParam/*!*/ block, object/*!*/ self, object other) {

            object i = self;
            object compare = null;
            var greaterThan = greaterThanStorage.GetCallSite(">");
            while (RubyOps.IsFalse(compare)) {
                // Rather than test i <= other we test !(i > other)
                compare = greaterThan.Target(greaterThan, i, other);

                // If the comparison failed (i.e. returned null) then we throw an error.
                if (compare == null) {
                    throw RubyExceptions.MakeComparisonError(greaterThanStorage.Context, i, other);
                }

                // If the comparison worked but returned false then we carry on
                if (RubyOps.IsFalse(compare)) {
                    object result;
                    if (block.Yield(i, out result)) {
                        return result;
                    }

                    var add = addStorage.GetCallSite("+");
                    i = add.Target(add, i, 1);
                }
            }
            return self;
        }

        #endregion

        #region downto

        /// <summary>
        /// Iterates block, passing decreasing values from self down to and including other, where both self and other are Fixnum.
        /// </summary>
        /// <returns>self</returns>
        /// <example>
        /// 5.downto(1) { |n| print n, ".. " }
        ///   print "  Liftoff!\n"
        /// produces: 
        /// 5.. 4.. 3.. 2.. 1..   Liftoff!
        /// </example>
        /// <remarks>
        /// Since both self and other are Fixnum then this algorithm doesn't need to worry about overflowing into Bignum.
        /// </remarks>
        [RubyMethod("downto")]
        public static object DownTo(BlockParam block, int self, int other) {
            return (block != null) ? DownToImpl(block, self, other) : new Enumerator(self, "downto", other);
        }

        private static object DownToImpl(BlockParam/*!*/ block, int self, int other) {
            int i = self;
            while (i >= other) {
                object result;
                if (block.Yield(i, out result)) {
                    return result;
                }
                i--;
            }
            return self;
        }

        /// <summary>
        /// Iterates block, passing decreasing values from self down to and including other, where other is not Fixnum (probably Bignum or Float).
        /// </summary>
        /// <returns>self</returns>
        /// <remarks>
        /// Dynamically invokes "-" operator to find next item down.
        /// Dynamically invokes "&lt;" operator and takes the negation to see if we have reached the bottom.
        /// This approach automatically deals with Floats and overflow/underflow between Fixnum and Bignum.
        /// </remarks>
        [RubyMethod("downto")]
        public static object DownTo(BinaryOpStorage/*!*/ lessThanStorage, BinaryOpStorage/*!*/ subtractStorage,
            BlockParam block, object/*!*/ self, object other) {

            return (block != null) ? DownToImpl(lessThanStorage, subtractStorage, block, self, other) :
                new Enumerator(self, "downto", other);
        }

        private static object DownToImpl(BinaryOpStorage/*!*/ lessThanStorage, BinaryOpStorage/*!*/ subtractStorage,
            BlockParam block, object/*!*/ self, object other) {
            object i = self;
            object compare = null;

            var lessThan = lessThanStorage.GetCallSite("<");
            while (RubyOps.IsFalse(compare)) {
                // Rather than test i >= other we test !(i < other)
                compare = lessThan.Target(lessThan, i, other);

                // If the comparison failed (i.e. returned null) then we throw an error.
                if (compare == null) {
                    throw RubyExceptions.MakeComparisonError(lessThanStorage.Context, i, other);
                }

                // If the comparison worked but returned false then we 
                if (RubyOps.IsFalse(compare)) {
                    object result;
                    if (block.Yield(i, out result)) {
                        return result;
                    }

                    var subtract = subtractStorage.GetCallSite("-");
                    i = subtract.Target(subtract, i, 1);
                }
            }
            return self;
        }

        #endregion

        #region gcd, lcm, gcdlcm

        private static int SignedGcd(int a, int b) {
            // avoid overflow (Int32.MinValue % -1)
            if (b == -1) {
                return -1;
            }

            while (b != 0) {
                int t = b;
                b = a % b;
                a = t;
            }

            return a;
        }

        private static BigInteger/*!*/ SignedGcd(BigInteger/*!*/ a, BigInteger/*!*/ b) {
            while (!b.IsZero()) {
                BigInteger t = b;
                b = a % b;
                a = t;
            }

            return a;
        }

        private static object/*!*/ Lcm(int self, int other, int gcd) {
            return gcd == 0 ? ClrInteger.Zero : Protocols.Normalize(Math.Abs((long)self / gcd * other));
        }

        private static object/*!*/ Lcm(BigInteger/*!*/ self, BigInteger/*!*/ other, BigInteger/*!*/ gcd) {
            return gcd == 0 ? ClrInteger.Zero : Protocols.Normalize((self / gcd * other).Abs());
        }

        [RubyMethod("gcd")]
        public static object/*!*/ Gcd(int self, int other) {
            return ClrInteger.Abs(SignedGcd(self, other));
        }

        [RubyMethod("gcd")]
        public static object/*!*/ Gcd(BigInteger/*!*/ self, BigInteger/*!*/ other) {
            return ClrInteger.Abs(SignedGcd(self, other));
        }

        [RubyMethod("gcd")]
        public static object/*!*/ Gcd(object/*!*/ self, object other) {
            throw RubyExceptions.CreateTypeError("not an integer");
        }

        [RubyMethod("lcm")]
        public static object/*!*/ Lcm(int self, int other) {
            return Lcm(self, other, SignedGcd(self, other));
        }

        [RubyMethod("lcm")]
        public static object/*!*/ Lcm(BigInteger/*!*/ self, BigInteger/*!*/ other) {
            return Lcm(self, other, SignedGcd(self, other));
        }

        [RubyMethod("lcm")]
        public static object/*!*/ Lcm(object/*!*/ self, object other) {
            throw RubyExceptions.CreateTypeError("not an integer");
        }

        [RubyMethod("gcdlcm")]
        public static RubyArray/*!*/ GcdLcm(int self, int other) {
            int gcd = SignedGcd(self, other);
            return new RubyArray { ClrInteger.Abs(gcd), Lcm(self, other, gcd) };
        }

        [RubyMethod("gcdlcm")]
        public static RubyArray/*!*/ GcdLcm(BigInteger/*!*/ self, BigInteger/*!*/ other) {
            BigInteger gcd = SignedGcd(self, other);
            return new RubyArray { ClrInteger.Abs(gcd), Lcm(self, other, gcd) };
        }

        [RubyMethod("gcdlcm")]
        public static RubyArray/*!*/ GcdLcm(object/*!*/ self, object other) {
            throw RubyExceptions.CreateTypeError("not an integer");
        }

        #endregion

        #region Helpers

        public static object TryUnaryMinus(object obj) {
            if (obj is int) {
                int i = (int)obj;
                return (i != Int32.MinValue) ? ScriptingRuntimeHelpers.Int32ToObject(-i) : ClrInteger.Narrow(-(long)i);
            }

            if (obj is long l) {
                return ClrInteger.NegateWide(l);
            }

            if (obj is BigInteger bignum) {
                return Protocols.Normalize(-bignum);
            }

            return null;
        }

        #endregion
    }
}
 
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
using System.Text;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;

namespace IronRuby.Runtime {

    /// <summary>
    /// ASCII-8BIT encoding: 
    /// 0xmn -> \u00mn
    /// \u00mn -> 0xmn, error otherwise
    /// </summary>
    /// <remarks>
    /// The mapping is exactly ISO-8859-1's, so the conversions are done by the runtime's
    /// vectorized Latin-1 widening and narrowing; only the check that every char is at most
    /// ÿ - where this encoding throws and Latin-1 would substitute '?' - is our own.
    /// The string and span overloads are overridden too: <see cref="Encoding"/>'s
    /// defaults for them copy into temporary arrays and go back through the array overloads.
    /// </remarks>
    public sealed class BinaryEncoding : Encoding {
        public static readonly Encoding/*!*/ Instance = new BinaryEncoding();

        private static readonly Encoding/*!*/ _latin1 = Encoding.Latin1;

        private BinaryEncoding()
            : base(RubyEncoding.CodePageBinary) {
        }

        private static void CheckBytes(ReadOnlySpan<char> chars) {
            if (chars.IndexOfAnyExceptInRange('\0', 'ÿ') >= 0) {
                // TODO: we don't support fallbacks (we should add the result of the fallback to the count):
                throw new EncoderFallbackException();
            }
        }

        private static int Narrow(ReadOnlySpan<char> chars, Span<byte> bytes) {
            CheckBytes(chars);
            return _latin1.GetBytes(chars, bytes);
        }

        #region Byte Count

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            ContractUtils.RequiresArrayRange(chars, index, count, "index", "count");
            CheckBytes(chars.AsSpan(index, count));
            return count;
        }

        public override int GetByteCount(string/*!*/ s) {
            ContractUtils.RequiresNotNull(s, "s");
            CheckBytes(s);
            return s.Length;
        }

        public override int GetByteCount(ReadOnlySpan<char> chars) {
            CheckBytes(chars);
            return chars.Length;
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            return count;
        }

        public override int GetCharCount(ReadOnlySpan<byte> bytes) {
            return bytes.Length;
        }

        public override int GetMaxByteCount(int charCount) {
            return charCount;
        }

        public override int GetMaxCharCount(int byteCount) {
            return byteCount;
        }

        #endregion

        #region Encoding

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            ContractUtils.RequiresArrayRange(chars, charIndex, charCount, "charIndex", "charCount");
            ContractUtils.RequiresArrayRange(bytes, byteIndex, charCount, "byteIndex", "charCount");
            return Narrow(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex, charCount));
        }

        public override int GetBytes(string/*!*/ s, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            ContractUtils.RequiresNotNull(s, "s");
            ContractUtils.RequiresArrayRange(s, charIndex, charCount, "charIndex", "charCount");
            ContractUtils.RequiresArrayRange(bytes, byteIndex, charCount, "byteIndex", "charCount");
            return Narrow(s.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex, charCount));
        }

        public override byte[]/*!*/ GetBytes(string/*!*/ s) {
            ContractUtils.RequiresNotNull(s, "s");
            CheckBytes(s);
            byte[] result = new byte[s.Length];
            _latin1.GetBytes(s, result);
            return result;
        }

        public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes) {
            return Narrow(chars, bytes);
        }

        #endregion

        #region Decoding

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            ContractUtils.RequiresArrayRange(bytes, byteIndex, byteCount, "byteIndex", "byteCount");
            ContractUtils.RequiresArrayRange(chars, charIndex, byteCount, "charIndex", "byteCount");
            return _latin1.GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex, byteCount));
        }

        public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars) {
            return _latin1.GetChars(bytes, chars);
        }

        public override string/*!*/ GetString(byte[]/*!*/ bytes, int index, int count) {
            ContractUtils.RequiresArrayRange(bytes, index, count, "index", "count");
            return _latin1.GetString(bytes, index, count);
        }

        #endregion

        public override string/*!*/ EncodingName {
            get { return "ASCII-8BIT"; }
        }

        public override bool IsSingleByte {
            get { return true; }
        }

        public override string/*!*/ WebName {
            get { return "ASCII-8BIT"; }
        }
    }
}

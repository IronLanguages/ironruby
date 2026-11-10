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
using System.Buffers;
using System.Collections.Generic;
using Microsoft.Scripting.Utils;
using System.Text;
using IronRuby.Runtime;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace IronRuby.Builtins {
    public partial class MutableString {
        /// <summary>
        /// Mutable byte array. 
        /// All indices and counts are in bytes.
        /// </summary>
        [Serializable]
        internal class BinaryContent : Content {
            // The bytes are _data[_start, _start + _count). _start is 0 unless this content shares
            // _data (a slice that does not begin at the start of the string, see GetSlice), so every
            // write - which gives the content an array of its own first, at offset 0 (EnterWrite) -
            // can index _data directly; everything else adds _start.
            protected byte[] _data;
            protected int _start;
            protected int _count;

            // Whether _data may be referenced by another content as well - a copy made by Clone
            // shares the array instead of copying it (copy-on-write; see MutableString.Content.cs) -
            // and whether a thread holds this content: see ContentState. Every method that writes to
            // _data does so between EnterWrite, which gives this content an array of its own, and
            // ExitWrite.
            private int _state;

            internal BinaryContent(byte[]/*!*/ data, MutableString owner) 
                : this(data, data.Length, owner) {
            }

            internal BinaryContent(byte[]/*!*/ data, int count, MutableString owner)
                : base(owner) {
                Assert.NotNull(data);
                Debug.Assert(count >= 0 && count <= data.Length);
                _data = data;
                _count = count;
            }

            // A content that shares data with another: see _state.
            private BinaryContent(byte[]/*!*/ data, int start, int count, MutableString owner)
                : base(owner) {
                Debug.Assert(start >= 0 && count >= 0 && count <= data.Length - start);
                _data = data;
                _start = start;
                _count = count;
                _state = ContentState.Shared;
                IsOffsetSlice = start != 0;
            }

            internal override Content/*!*/ Unslice() {
                return IsOffsetSlice ? new BinaryContent(ToByteArray(), _owner) : this;
            }

            /// <summary>
            /// Holds this content for a write until <see cref="ExitWrite"/>, and gives it an array of its
            /// own if it shares one, with room for at least <paramref name="minCapacity"/> bytes so that
            /// an append does not copy a second time.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void EnterWrite(int minCapacity) {
                if (!ContentState.TryEnterUnshared(ref _state)) {
                    EnterWriteSlow(minCapacity);
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void EnterWriteSlow(int minCapacity) {
                // An offset slice is replaced before it is written (see IsOffsetSlice); copying it
                // to offset 0 here as well keeps a missed path correct, if not safe for readers.
                Debug.Assert(!IsOffsetSlice, "an offset slice is written in place");
                if (ContentState.Enter(ref _state)) {
                    try {
                        // Room past the end is zeroed, as a fresh array's would be; the rest is copied over.
                        var data = Utils.NewArrayToFill<byte>(Math.Max(_count, minCapacity));
                        Buffer.BlockCopy(_data, _start, data, 0, _count);
                        Array.Clear(data, _count, data.Length - _count);
                        _data = data;
                        _start = 0;
                    } catch {
                        ContentState.Exit(ref _state, true);
                        throw;
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void ExitWrite() {
                ContentState.Exit(ref _state, false);
            }

            internal override void PrepareForWrite(int minCapacity) {
                EnterWrite(minCapacity);
                ExitWrite();
            }

            internal ReadOnlySpan<byte> GetDataSpan() {
                // A write on another thread can replace the array between these reads (copy-on-write,
                // growing): the span never reaches past the array actually read. (An offset slice's
                // array and offset do not change at all, see IsOffsetSlice.)
                byte[] data = _data;
                int start = _start;
                return new ReadOnlySpan<byte>(data, start, Math.Min(_count, data.Length - start));
            }

            protected virtual BinaryContent/*!*/ Create(byte[]/*!*/ data, MutableString owner) {
                return new BinaryContent(data, owner);
            }

            // TODO: we can remember both representations until a mutable operation is performed
            /// <exception cref="DecoderFallbackException">Invalid character.</exception>
            private CharArrayContent/*!*/ SwitchToChars() {
                return SwitchToChars(0);
            }

            /// <exception cref="DecoderFallbackException">Invalid character.</exception>
            private CharArrayContent/*!*/ SwitchToChars(int additionalCapacity) {
                char[] chars = DataToChars(additionalCapacity, _owner._encoding.EscapingEncoding);
                return WrapContent(chars, chars.Length - additionalCapacity);
            }

            /// <exception cref="DecoderFallbackException">Invalid character.</exception>
            private char[]/*!*/ DataToChars(int additionalCapacity, Encoding/*!*/ encoding) {
                if (_count == 0) {
                    return (additionalCapacity == 0) ? Utils.EmptyChars : new char[additionalCapacity];
                } else if (additionalCapacity == 0) {
                    return encoding.GetChars(_data, _start, _count);
                } else {
                    var result = new char[encoding.GetCharCount(_data, _start, _count) + additionalCapacity];
                    encoding.GetChars(_data, _start, _count, result, 0);
                    return result;
                }
            }

            private string/*!*/ DataToString() {
                if (_count == 0) {
                    return String.Empty;
                } else {
                    return _owner._encoding.EscapingEncoding.GetString(_data, _start, _count);
                } 
            }

            internal void AppendBytes(string/*!*/ str, int start, int count) {
                EnterWrite(_count + count);
                try {
                    _count = Utils.Append(ref _data, _count, str, start, count, _owner._encoding.EscapingEncoding);
                } finally {
                    ExitWrite();
                }
            }

            internal void AppendBytes(char[]/*!*/ chars, int start, int count) {
                EnterWrite(_count + count);
                try {
                    _count = Utils.Append(ref _data, _count, chars, start, count, _owner._encoding.EscapingEncoding);
                } finally {
                    ExitWrite();
                }
            }

            #region UpdateCharacterFlags, CalculateHashCode, Length, Clone, Count (read-only)

            public override uint UpdateCharacterFlags(uint flags) {
                // can't determine surrogates for binary repr (w/o conversion):
                if (System.Text.Ascii.IsValid(GetDataSpan())) {
                    return (flags & ~MutableString.AsciiUnknownFlag) | MutableString.IsAsciiFlag;
                } else {
                    return flags & ~(MutableString.AsciiUnknownFlag | MutableString.IsAsciiFlag);
                }
            }

            [SkipLocalsInit]
            public override int CalculateHashCode() {
                // The same string can be held as bytes or as characters and the hash has to come
                // out the same either way (see Content.HashCharacters): long ASCII hashes its bytes,
                // anything else the characters the escaping decoder makes of them - those
                // CharArrayContent would hash. They go to a scratch buffer rather than a new string:
                // a long string was copied to the large object heap on every #hash.
                var bytes = GetDataSpan();
                RubyEncoding encoding = _owner._encoding;
                bool ascii;
                if (_count >= AsciiByteHashMinLength) {
                    // the bytes themselves, not the cached flags, which a write in place can leave behind
                    ascii = encoding.AsciiRoundTrips && System.Text.Ascii.IsValid(bytes);
                    if (ascii) {
                        return HashAsciiBytes(bytes);
                    }
                } else {
                    ascii = (encoding == RubyEncoding.UTF8 || encoding == RubyEncoding.Binary || encoding == RubyEncoding.Ascii) &&
                        System.Text.Ascii.IsValid(bytes);
                }
                int charCount = ascii ? _count : encoding.EscapingEncoding.GetCharCount(_data, _start, _count);

                // EscapingEncoding implements only the array overloads: the span ones would go
                // through temporary arrays of their own.
                char[] rented = null;
                Span<char> chars = (ascii && charCount <= AsciiByteHashMinLength) ? stackalloc char[AsciiByteHashMinLength] : (rented = ArrayPool<char>.Shared.Rent(charCount));
                try {
                    if (ascii) {
                        System.Text.Ascii.ToUtf16(bytes, chars, out _);
                    } else {
                        encoding.EscapingEncoding.GetChars(_data, _start, _count, rented, 0);
                    }
                    // Characters decoded in an encoding whose ASCII bytes are not its ASCII characters
                    // (UTF-16, a dummy encoding) can still be long ASCII, which is hashed narrowed -
                    // as the characters of the same string held as characters, or of a CLR string, are.
                    return HashCharacters(chars.Slice(0, charCount), !encoding.AsciiRoundTrips);
                } finally {
                    if (rented != null) {
                        ArrayPool<char>.Shared.Return(rented);
                    }
                }
            }

            public override int Count {
                get { return _count; }
                set {
                    EnterWrite(value);
                    try {
                        if (_data.Length < value) {
                            Array.Resize(ref _data, Utils.GetExpandedSize(_data, value));
                        } else {
                            Utils.Fill(_data, _count, (byte)0, value - _count);
                        }
                        _count = value;
                    } finally {
                        ExitWrite();
                    }
                }
            }

            public override bool IsEmpty {
                get { return _count == 0; }
            }

            public override int GetCharCount() {
                return (_owner.HasSingleByteCharacters || _count == 0) ? _count : SwitchToChars().GetCharCount();
            }

            public override int GetCharacterCount() {
                if (_owner.HasSingleByteCharacters || _count == 0) {
                    return _count;
                }

                // In UTF-16 and UTF-32 a character is measured in code units, so bytes that
                // decode to nothing count one per unit rather than one per byte: a lone
                // surrogate is one character, not two.
                int unit = _owner._encoding.MinBytesPerChar;
                if (unit > 1) {
                    return CountUnicodeUnits(unit, _owner._encoding.IsBigEndianUnicode);
                }

                // Valid UTF-8 is counted where it is: switching to characters costs a copy, and a
                // caller alternating #length and #bytesize would switch back and forth every time.
                // Every character is one UTF-16 unit except those of four bytes, which are two.
                if (_owner._encoding == RubyEncoding.UTF8) {
                    var span = GetDataSpan();
                    if (System.Text.Unicode.Utf8.IsValid(span)) {
                        int result = System.Text.Encoding.UTF8.GetCharCount(span);
                        int i = 0;
                        int j;
                        while ((j = span.Slice(i).IndexOfAnyInRange((byte)0xF0, (byte)0xF7)) >= 0) {
                            result--;
                            i += j + 1;
                        }
                        return result;
                    }
                }

                // This used to decode by hand so that a *run* of undecodable bytes could be counted
                // as one character. MRI counts each such byte separately - "a\xE3\x81c" is four
                // characters in UTF-8, not three - and the escaping decoder produces exactly one
                // character per undecodable byte, so the ordinary path is both simpler and right.
                return SwitchToChars().GetCharacterCount();
            }

            /// <summary>
            /// Counts the characters of a UTF-16 or UTF-32 string by walking its code units. A
            /// well formed surrogate pair is one character of two units; anything else - a lone
            /// surrogate, a code point out of range, a unit cut short at the end of the string -
            /// is one character of a single unit, which is how MRI counts them.
            /// </summary>
            private int CountUnicodeUnits(int unit, bool bigEndian) {
                int count = 0;
                int i = 0;
                while (i < _count) {
                    if (_count - i < unit) {
                        // A trailing fragment too short to be a unit still counts as a character.
                        count++;
                        break;
                    }

                    if (unit == 2 && IsHighSurrogate(ReadUnit16(i, bigEndian)) &&
                        _count - i >= 4 && IsLowSurrogate(ReadUnit16(i + 2, bigEndian))) {
                        i += 4;
                    } else {
                        i += unit;
                    }
                    count++;
                }
                return count;
            }

            private int ReadUnit16(int i, bool bigEndian) {
                i += _start;
                return bigEndian ? (_data[i] << 8) | _data[i + 1] : (_data[i + 1] << 8) | _data[i];
            }

            private static bool IsHighSurrogate(int unit) {
                return unit >= 0xD800 && unit <= 0xDBFF;
            }

            private static bool IsLowSurrogate(int unit) {
                return unit >= 0xDC00 && unit <= 0xDFFF;
            }

            public override int GetByteCount() {
                return _count;
            }

            // Shares the array: see _state.
            public override Content/*!*/ Clone() {
                ContentState.Enter(ref _state);
                try {
                    return new BinaryContent(_data, _start, _count, _owner);
                } finally {
                    ContentState.Exit(ref _state, true);
                }
            }

            public override void TrimExcess() {
                EnterWrite(0);
                try {
                    Utils.TrimExcess(ref _data, _count);
                } finally {
                    ExitWrite();
                }
            }

            public override int GetCapacity() {
                return _data.Length - _start;
            }

            public override void SetCapacity(int capacity) {
                if (capacity < _count) {
                    throw new InvalidOperationException();
                }
                EnterWrite(capacity);
                try {
                    Array.Resize(ref _data, capacity);
                } finally {
                    ExitWrite();
                }
            }

            #endregion

            #region Conversions (read-only)

            public override string/*!*/ ConvertToString() {
                return SwitchToChars().ConvertToString();
            }

            public override byte[]/*!*/ ConvertToBytes() {
                return ToByteArray();
            }

            public override string/*!*/ ToString() {
                return DataToString();
            }

            public override byte[]/*!*/ ToByteArray() {
                return _data.GetSlice(_start, _count);
            }

            internal override byte[]/*!*/ GetByteArray(out int start, out int count) {
                start = _start;
                count = _count;
                return _data;
            }

            public override Content/*!*/ SwitchToBinaryContent() {
                return this;
            }

            public override Content/*!*/ SwitchToStringContent() {
                return SwitchToChars();
            }

            public override Content/*!*/ SwitchToMutableContent() {
                return this;
            }

            public override Content/*!*/ EscapeRegularExpression() {
                if (!_owner._encoding.IsAsciiIdentity && !ContainsInvalidCharacters()) {
                    // UTF-16 and UTF-32: a byte is not a character there, and escaping the bytes
                    // one by one put a lone '\' byte in the middle of the code units ("." became
                    // the three bytes "\\.\0", which no longer matched a ".").
                    StringBuilder sb = RubyRegex.EscapeToStringBuilder(_owner._encoding.StrictEncoding.GetString(_data, _start, _count));
                    return (sb != null) ? new StringContent(sb.ToString(), _owner) : Clone();
                }
                // TODO:
                var a = ToByteArray();
                return Create(
                    BinaryEncoding.Instance.GetBytes(RubyRegex.Escape(BinaryEncoding.Instance.GetString(a, 0, a.Length))),
                    _owner
                );
            }

            public override void CheckEncoding() {
                _owner._encoding.StrictEncoding.GetCharCount(_data, _start, _count);
            }

            public override bool ContainsInvalidCharacters() {
                return Utils.ContainsInvalidCharacters(_data, _start, _count, _owner._encoding.StrictEncoding);
            }

            #endregion

            #region CompareTo (read-only)

            public override int OrdinalCompareTo(string/*!*/ str) {
                if (_owner.HasByteCharacters) {
                    return Utils.ValueCompareTo(GetDataSpan(), str);
                } else {
                    return SwitchToChars().OrdinalCompareTo(str);
                }
            }

            internal int OrdinalCompareTo(ReadOnlySpan<byte> bytes) {
                return Utils.ValueCompareTo(GetDataSpan(), bytes);
            }

            // this <=> content
            public override int OrdinalCompareTo(Content/*!*/ content) {
                return content.ReverseOrdinalCompareTo(this);
            }

            // content.bytes <=> this.bytes
            public override int ReverseOrdinalCompareTo(BinaryContent/*!*/ content) {
                return content.OrdinalCompareTo(GetDataSpan());
            }

            // content.chars <=> this.bytes
            public override int ReverseOrdinalCompareTo(CharArrayContent/*!*/ content) {
                return content.SwitchToBinary().OrdinalCompareTo(GetDataSpan());
            }

            // content.chars <=> this.bytes
            public override int ReverseOrdinalCompareTo(StringContent/*!*/ content) {
                return content.SwitchToBinary().OrdinalCompareTo(GetDataSpan());
            }

            #endregion

            #region Slices (read-only)

            public override char GetChar(int index) {
                if (_owner.HasSingleByteCharacters || index == 0) {
                    // character index == byte index (and the bytes may be a slice of a larger array)
                    if ((uint)index >= (uint)_count) {
                        throw new IndexOutOfRangeException();
                    }
                    var result = _data[_start + index];
                    if (result < 0x80 || _owner.HasByteCharacters) {
                        // no decoding needed:
                        return (char)result;
                    }
                }

                // TODO: invalid characters
                return SwitchToChars().GetChar(index);
            }

            public override byte GetByte(int index) {
                if ((uint)index >= (uint)_count) {
                    throw new IndexOutOfRangeException();
                }
                return _data[_start + index];
            }

            public override string/*!*/ GetStringSlice(int start, int count) {
                return SwitchToChars().GetStringSlice(start, count);
            }

            public override byte[]/*!*/ GetBinarySlice(int start, int count) {
                return Utils.GetSlice(GetDataSpan(), start, count);
            }

            public override Content/*!*/ GetSlice(int start, int count) {
                if (Utils.IsSharedSlice(_data.Length, Utils.NormalizeCount(_count, start, count))) {
                    // see Utils.IsSharedSlice; checked again while holding the content
                    bool shared = ContentState.Enter(ref _state);
                    try {
                        int sliceCount = Utils.NormalizeCount(_count, start, count);
                        if (Utils.IsSharedSlice(_data.Length, sliceCount)) {
                            shared = true;
                            return new BinaryContent(_data, _start + start, sliceCount, _owner);
                        }
                    } finally {
                        ContentState.Exit(ref _state, shared);
                    }
                }
                return Create(Utils.GetSlice(GetDataSpan(), start, count), _owner);
            }

            public override CharacterEnumerator/*!*/ GetCharacters() {
                if (_owner.HasByteCharacters) {
                    return new MutableString.BinaryCharacterEnumerator(_owner.Encoding, _data, _start, _count);
                }

                // EnumerateAsCharacters used to be needed here because the bytes might not decode;
                // it grouped each run of undecodable bytes into one character, which is not how MRI
                // counts them. The escaping decoder gives one character per undecodable byte and
                // hands the byte back unchanged on the way out, so the ordinary character content
                // now serves - and it gets cached for the next operation into the bargain.
                return SwitchToChars().GetCharacters();
            }

            public override IEnumerable<byte>/*!*/ GetBytes() {
                return Utils.Enumerate(_data, _start, _count);
            }

            #endregion

            #region StartsWith (read-only)

            public override bool StartsWith(char c) {
                if (_count == 0) {
                    return false;
                }

                if (_owner.HasByteCharacters || c < 0x80 && _owner._encoding.IsAsciiIdentity) {
                    return _data[_start] == c;
                }

                byte[] bytes = new byte[_owner.Encoding.MaxBytesPerChar];
                int byteCount = _owner.Encoding.EscapingEncoding.GetBytes(new char[] { c }, 0, 1, bytes, 0);
                if (byteCount > _count) {
                    return false;
                }

                return GetDataSpan().StartsWith(new ReadOnlySpan<byte>(bytes, 0, byteCount));
            }

            #endregion

            #region IndexOf (read-only)

            public override int IndexOf(char c, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.IndexOf(GetDataSpan(), c, start, count);
                }
                try {
                    return SwitchToChars().IndexOf(c, start, count);
                } catch (DecoderFallbackException) when (c < 0x80) {
                    // The content is not a valid character sequence in its encoding, so it
                    // cannot be read as characters at all. MRI searches the bytes, which is
                    // what makes String#each_line work on such a string - and each_line is
                    // what pp walks a string with, so without this a failure message about a
                    // string with a stray byte in it is a decoder error instead of the string.
                    return Utils.IndexOf(GetDataSpan(), c, start, count);
                }
            }

            public override int IndexOf(byte b, int start, int count) {
                count = Utils.NormalizeCount(_count, start, count);
                if (count <= 0) {
                    return -1;
                }
                int i = GetDataSpan().Slice(start, count).IndexOf(b);
                return i < 0 ? -1 : start + i;
            }

            public override int IndexOf(string/*!*/ str, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.IndexOf(GetDataSpan(), str, start, count);
                }
                try {
                    return SwitchToChars().IndexOf(str, start, count);
                } catch (DecoderFallbackException) when (str.IsAscii()) {
                    // See IndexOf(char): the bytes are all that is left to search. Only an
                    // ASCII needle can be matched against them byte for byte.
                    return Utils.IndexOf(GetDataSpan(), str, start, count);
                }
            }

            public override int IndexOf(byte[]/*!*/ bytes, int start, int count) {
                return Utils.IndexOf(GetDataSpan(), bytes, start, count);
            }

            public override int IndexIn(Content/*!*/ str, int start, int count) {
                // The buffer is a capacity, not a length: searching for the whole of it would look
                // for the unused tail as well, and a needle built by growing one - String#pack, say -
                // would never be found.
                return str.IndexOf(ExactData(), start, count);
            }

            /// <summary>
            /// The bytes of the string, without the unused tail of the buffer. No copy is made when
            /// the buffer happens to be exactly full.
            /// </summary>
            private byte[]/*!*/ ExactData() {
                return (_start == 0 && _count == _data.Length) ? _data : ToByteArray();
            }

            #endregion

            #region LastIndexOf (read-only)

            public override int LastIndexOf(char c, int start, int count) {
                return SwitchToChars().LastIndexOf(c, start, count);
            }

            public override int LastIndexOf(byte b, int start, int count) {
                Utils.NormalizeLastIndexOfIndices(_count, ref start, ref count);
                if (count <= 0) {
                    return -1;
                }
                int finish = start - count + 1;
                int i = GetDataSpan().Slice(finish, count).LastIndexOf(b);
                return i < 0 ? -1 : finish + i;
            }

            public override int LastIndexOf(string/*!*/ str, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.LastIndexOf(GetDataSpan(), str, start, count);
                } else {
                    return SwitchToChars().LastIndexOf(str, start, count);
                }
            }

            public override int LastIndexOf(byte[]/*!*/ bytes, int start, int count) {
                // Array.LastIndexOf has different semantics for indices than String.LastIndexOf:
                return Utils.LastIndexOf(GetDataSpan(), bytes, start, count);
            }

            public override int LastIndexIn(Content/*!*/ str, int start, int count) {
                return str.LastIndexOf(ExactData(), start, count);
            }

            #endregion

            #region Concatenate (read-only)

            public override Content/*!*/ Concat(Content/*!*/ content) {
                return content.ConcatTo(this);
            }

            // Every byte of the result is written, so the array is not zeroed first (see Utils.NewArrayToFill).
            internal BinaryContent/*!*/ Concatenate(CharArrayContent/*!*/ content) {
                int count = content.GetDataByteCount();
                var result = Utils.NewArrayToFill<byte>(_count + count);
                GetDataSpan().CopyTo(result);
                content.GetDataBytes(result, _count);
                return Create(result, null);
            }

            internal BinaryContent/*!*/ Concatenate(StringContent/*!*/ content) {
                int count = content.GetDataByteCount();
                var result = Utils.NewArrayToFill<byte>(_count + count);
                GetDataSpan().CopyTo(result);
                content.GetDataBytes(result, _count);
                return Create(result, null);
            }

            // binary + binary(self) -> binary
            public override Content/*!*/ ConcatTo(BinaryContent/*!*/ content) {
                var result = Utils.NewArrayToFill<byte>(content._count + _count);
                content.GetDataSpan().CopyTo(result);
                GetDataSpan().CopyTo(result.AsSpan(content._count));
                return Create(result, null);
            }

            // chars + binary(self) -> binary
            public override Content/*!*/ ConcatTo(CharArrayContent/*!*/ content) {
                int count = content.GetDataByteCount();
                var result = Utils.NewArrayToFill<byte>(count + _count);
                content.GetDataBytes(result, 0);
                GetDataSpan().CopyTo(result.AsSpan(count));
                return Create(result, null);
            }

            // string + binary(self) -> binary
            public override Content/*!*/ ConcatTo(StringContent/*!*/ content) {
                int count = content.GetDataByteCount();
                var result = Utils.NewArrayToFill<byte>(count + _count);
                content.GetDataBytes(result, 0);
                GetDataSpan().CopyTo(result.AsSpan(count));
                return Create(result, null);
            }

            #endregion

            #region Append

            public override void Append(char c, int repeatCount) {
                if (_owner.HasByteCharacters || c < 0x80 && _owner._encoding.IsAsciiIdentity) {
                    Append((byte)c, repeatCount);
                } else {
                    EnterWrite(_count + repeatCount);
                    try {
                        _count = Utils.Append(ref _data, _count, c, repeatCount, _owner._encoding.EscapingEncoding);
                    } finally {
                        ExitWrite();
                    }
                }
            }

            public override void Append(byte b, int repeatCount) {
                EnterWrite(_count + repeatCount);
                try {
                    _count = Utils.Append(ref _data, _count, b, repeatCount);
                } finally {
                    ExitWrite();
                }
            }

            public override void Append(string/*!*/ str, int start, int count) {
                AppendBytes(str, start, count);
            }

            public override void Append(char[]/*!*/ chars, int start, int count) {
                AppendBytes(chars, start, count);
            }

            public override void Append(byte[]/*!*/ bytes, int start, int count) {
                EnterWrite(_count + count);
                try {
                    _count = Utils.Append(ref _data, _count, bytes, start, count);
                } finally {
                    ExitWrite();
                }
            }

            public override void Append(Stream/*!*/ stream, int count) {
                // Read outside the content, which a write holds without reentry: a stream's Read is code of any
                // kind - a pipe or socket that blocks a #dup of this string for as long as it waits, or a Read
                // that copies this very string, which would wait for itself.
                byte[] buffer = ArrayPool<byte>.Shared.Rent(count);
                try {
                    int read = stream.Read(buffer, 0, count);
                    Append(buffer, 0, read);
                } finally {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            public override void AppendFormat(IFormatProvider provider, string/*!*/ format, object[]/*!*/ args) {
                var formatted = String.Format(provider, format, args);
                AppendBytes(formatted, 0, formatted.Length);
            }

            // this + content[start, count]
            public override void Append(Content/*!*/ content, int start, int count) {
                content.AppendTo(this, start, count);
            }

            // content.bytes + this.bytes[start, count]
            public override void AppendTo(BinaryContent/*!*/ content, int start, int count) {
                if (start < 0 || start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }
                
                content.Append(_data, _start + start, count);
            }

            // content.chars + this.bytes[start, count]
            public override void AppendTo(CharArrayContent/*!*/ content, int start, int count) {
                if (start < 0 || start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.SwitchToBinary().Append(_data, _start + start, count);
            }

            // content.chars + this.bytes[start, count]
            public override void AppendTo(StringContent/*!*/ content, int start, int count) {
                if (start < 0 || start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.SwitchToBinary().Append(_data, _start + start, count);
            }

            #endregion

            #region Insert

            public override void Insert(int index, char c) {
                if (_owner.HasByteCharacters || c < 0x80 && _owner._encoding.IsAsciiIdentity) {
                    Insert(index, (byte)c);
                } else {
                    SwitchToChars(1).Insert(index, c);
                }
            }

            public override void Insert(int index, byte b) {
                EnterWrite(_count + 1);
                try {
                    _count = Utils.InsertAt(ref _data, _count, index, b, 1);
                } finally {
                    ExitWrite();
                }
            }

            public override void Insert(int index, string/*!*/ str, int start, int count) {
                SwitchToChars(count).Insert(index, str, start, count);
            }

            public override void Insert(int index, char[]/*!*/ chars, int start, int count) {
                SwitchToChars(count).Insert(index, chars, start, count);
            }

            public override void Insert(int index, byte[]/*!*/ bytes, int start, int count) {
                EnterWrite(_count + count);
                try {
                    _count = Utils.InsertAt(ref _data, _count, index, bytes, start, count);
                } finally {
                    ExitWrite();
                }
            }

            public override void InsertTo(Content/*!*/ str, int index, int start, int count) {
                if (start < 0 || count < 0 || start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }
                str.Insert(index, _data, _start + start, count);
            }

            public override void SetByte(int index, byte b) {
                if (index >= _count) {
                    throw new IndexOutOfRangeException();
                }
                EnterWrite(0);
                try {
                    _data[index] = b;
                } finally {
                    ExitWrite();
                }
            }

            public override void SetChar(int index, char c) {
                if (_owner.HasByteCharacters || c < 0x80 && _owner.HasSingleByteCharacters) {
                    SetByte(index, (byte)c);
                } else {
                    SwitchToChars().DataSetChar(index, c);
                }
            }

            #endregion

            #region Remove, Write

            public override void Remove(int start, int count) {
                bool shared = ContentState.Enter(ref _state);
                try {
                    if (shared) {
                        // Cutting off the end leaves the shared bytes as they are; anything else copies
                        // what is left in one go rather than copying everything and then moving the tail.
                        _count = Utils.RemoveShared(ref _data, ref _start, _count, start, count, ref shared);
                    } else {
                        _count = Utils.Remove(ref _data, _count, start, count);
                    }
                } finally {
                    ContentState.Exit(ref _state, shared);
                }
            }

            public override void Write(int offset, byte[]/*!*/ value, int start, int count) {
                EnterWrite(offset + count);
                try {
                    Utils.Resize(ref _data, offset + count);
                    _count = Math.Max(_count, offset + count);
                    Buffer.BlockCopy(value, start, _data, offset, count);
                } finally {
                    ExitWrite();
                }
            }

            public override void Write(int offset, byte value, int repeatCount) {
                int end = offset + repeatCount;
                EnterWrite(end);
                try {
                    Utils.Resize(ref _data, end);
                    if (end > _count) {
                        _count = end;
                    }
                    for (int i = offset; i < end; i++) {
                        _data[i] = value;
                    }
                } finally {
                    ExitWrite();
                }
            }

            #endregion
        }
    }
}

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
using Microsoft.Scripting.Utils;
using System.Text;
using IronRuby.Compiler;
using IronRuby.Runtime;
using System.Diagnostics;
using System.IO;

namespace IronRuby.Builtins {
    public partial class MutableString {
        /// <summary>
        /// Mutable byte array. 
        /// All indices and counts are in bytes.
        /// </summary>
        [Serializable]
        internal class BinaryContent : Content {
            protected byte[] _data;
            protected int _count;

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
                    return encoding.GetChars(_data, 0, _count);
                } else {
                    var result = new char[encoding.GetCharCount(_data, 0, _count) + additionalCapacity];
                    encoding.GetChars(_data, 0, _count, result, 0);
                    return result;
                }
            }

            private string/*!*/ DataToString() {
                if (_count == 0) {
                    return String.Empty;
                } else {
                    return _owner._encoding.EscapingEncoding.GetString(_data, 0, _count);
                } 
            }

            internal void AppendBytes(string/*!*/ str, int start, int count) {
                _count = Utils.Append(ref _data, _count, str, start, count, _owner._encoding.EscapingEncoding);
            }

            internal void AppendBytes(char[]/*!*/ chars, int start, int count) {
                _count = Utils.Append(ref _data, _count, chars, start, count, _owner._encoding.EscapingEncoding);
            }

            #region UpdateCharacterFlags, CalculateHashCode, Length, Clone, Count (read-only)

            public override uint UpdateCharacterFlags(uint flags) {
                // can't determine surrogates for binary repr (w/o conversion):
                if (_data.IsAscii(_count)) {
                    return (flags & ~MutableString.AsciiUnknownFlag) | MutableString.IsAsciiFlag;
                } else {
                    return flags & ~(MutableString.AsciiUnknownFlag | MutableString.IsAsciiFlag);
                }
            }

            public override int CalculateHashCode() {
                if (_count == 0) {
                    return String.Empty.GetHashCode();
                }

                // The same string can be held as bytes or as characters and the hash has to come
                // out the same either way, so this has to be the character representation the
                // escaping decoder produces - the same one CharArrayContent would hash.
                return DataToString().GetHashCode();
            }

            public override int Count {
                get { return _count; }
                set {
                    if (_data.Length < value) {
                        Array.Resize(ref _data, Utils.GetExpandedSize(_data, value));
                    } else {
                        Utils.Fill(_data, _count, (byte)0, value - _count);
                    }
                    _count = value;
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
                    var span = new ReadOnlySpan<byte>(_data, 0, _count);
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

            public override Content/*!*/ Clone() {
                return Create(ToByteArray(), _owner);
            }

            public override void TrimExcess() {
                Utils.TrimExcess(ref _data, _count);
            }

            public override int GetCapacity() {
                return _data.Length;
            }

            public override void SetCapacity(int capacity) {
                if (capacity < _count) {
                    throw new InvalidOperationException();
                }
                Array.Resize(ref _data, capacity);
            }

            #endregion

            #region Conversions (read-only)

            public override string/*!*/ ConvertToString() {
                return SwitchToChars().ConvertToString();
            }

            public override byte[]/*!*/ ConvertToBytes() {
                return _data.GetSlice(0, _count);
            }

            public override string/*!*/ ToString() {
                return DataToString();
            }

            public override byte[]/*!*/ ToByteArray() {
                return _data.GetSlice(0, _count);
            }

            internal override byte[]/*!*/ GetByteArray(out int count) {
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
                // TODO:
                var a = ToByteArray();
                return Create(
                    BinaryEncoding.Instance.GetBytes(RubyRegex.Escape(BinaryEncoding.Instance.GetString(a, 0, a.Length))),
                    _owner
                );
            }

            public override void CheckEncoding() {
                _owner._encoding.StrictEncoding.GetCharCount(_data, 0, _count);
            }

            public override bool ContainsInvalidCharacters() {
                return Utils.ContainsInvalidCharacters(_data, 0, _count, _owner._encoding.StrictEncoding);
            }

            #endregion

            #region CompareTo (read-only)

            public override int OrdinalCompareTo(string/*!*/ str) {
                if (_owner.HasByteCharacters) {
                    return Utils.ValueCompareTo(_data, _count, str);
                } else {
                    return SwitchToChars().OrdinalCompareTo(str);
                }
            }

            internal int OrdinalCompareTo(byte[]/*!*/ bytes, int count) {
                return _data.ValueCompareTo(_count, bytes, count);
            }

            // this <=> content
            public override int OrdinalCompareTo(Content/*!*/ content) {
                return content.ReverseOrdinalCompareTo(this);
            }

            // content.bytes <=> this.bytes
            public override int ReverseOrdinalCompareTo(BinaryContent/*!*/ content) {
                return content.OrdinalCompareTo(_data, _count);
            }

            // content.chars <=> this.bytes
            public override int ReverseOrdinalCompareTo(CharArrayContent/*!*/ content) {
                return content.SwitchToBinary().OrdinalCompareTo(_data, _count);
            }

            // content.chars <=> this.bytes
            public override int ReverseOrdinalCompareTo(StringContent/*!*/ content) {
                return content.SwitchToBinary().OrdinalCompareTo(_data, _count);
            }

            #endregion

            #region Slices (read-only)

            public override char GetChar(int index) {
                if (_owner.HasSingleByteCharacters || index == 0) {
                    // character index == byte index
                    if (index >= _count) {
                        throw new IndexOutOfRangeException();
                    }
                    var result = _data[index];
                    if (result < 0x80 || _owner.HasByteCharacters) {
                        // no decoding needed:
                        return (char)result;
                    }
                }

                // TODO: invalid characters
                return SwitchToChars().GetChar(index);
            }

            public override byte GetByte(int index) {
                if (index >= _count) {
                    throw new IndexOutOfRangeException();
                }
                return _data[index];
            }

            public override string/*!*/ GetStringSlice(int start, int count) {
                return SwitchToChars().GetStringSlice(start, count);
            }

            public override byte[]/*!*/ GetBinarySlice(int start, int count) {
                return _data.GetSlice(_count, start, count);
            }

            public override Content/*!*/ GetSlice(int start, int count) {
                return Create(_data.GetSlice(_count, start, count), _owner);
            }

            public override CharacterEnumerator/*!*/ GetCharacters() {
                if (_owner.HasByteCharacters) {
                    return new MutableString.BinaryCharacterEnumerator(_owner.Encoding, _data, _count);
                }

                // EnumerateAsCharacters used to be needed here because the bytes might not decode;
                // it grouped each run of undecodable bytes into one character, which is not how MRI
                // counts them. The escaping decoder gives one character per undecodable byte and
                // hands the byte back unchanged on the way out, so the ordinary character content
                // now serves - and it gets cached for the next operation into the bargain.
                return SwitchToChars().GetCharacters();
            }

            public override IEnumerable<byte>/*!*/ GetBytes() {
                return Utils.Enumerate(_data, _count);
            }

            #endregion

            #region StartsWith (read-only)

            public override bool StartsWith(char c) {
                if (_count == 0) {
                    return false;
                }

                if (_owner.HasByteCharacters || c < 0x80 && _owner._encoding.IsAsciiIdentity) {
                    return _data[0] == c;    
                }

                byte[] bytes = new byte[_owner.Encoding.MaxBytesPerChar];
                int byteCount = _owner.Encoding.EscapingEncoding.GetBytes(new char[] { c }, 0, 1, bytes, 0);
                if (byteCount > _count) {
                    return false;
                }

                return Utils.ValueCompareTo(_data, byteCount, bytes, byteCount) == 0;
            }

            #endregion

            #region IndexOf (read-only)

            public override int IndexOf(char c, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.IndexOf(_data, _count, c, start, count);
                }
                try {
                    return SwitchToChars().IndexOf(c, start, count);
                } catch (DecoderFallbackException) when (c < 0x80) {
                    // The content is not a valid character sequence in its encoding, so it
                    // cannot be read as characters at all. MRI searches the bytes, which is
                    // what makes String#each_line work on such a string - and each_line is
                    // what pp walks a string with, so without this a failure message about a
                    // string with a stray byte in it is a decoder error instead of the string.
                    return Utils.IndexOf(_data, _count, c, start, count);
                }
            }

            public override int IndexOf(byte b, int start, int count) {
                count = Utils.NormalizeCount(_count, start, count);
                return count > 0 ? Array.IndexOf(_data, b, start, count) : -1;
            }

            public override int IndexOf(string/*!*/ str, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.IndexOf(_data, _count, str, start, count);
                }
                try {
                    return SwitchToChars().IndexOf(str, start, count);
                } catch (DecoderFallbackException) when (str.IsAscii()) {
                    // See IndexOf(char): the bytes are all that is left to search. Only an
                    // ASCII needle can be matched against them byte for byte.
                    return Utils.IndexOf(_data, _count, str, start, count);
                }
            }

            public override int IndexOf(byte[]/*!*/ bytes, int start, int count) {
                return Utils.IndexOf(_data, _count, bytes, start, count);
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
                return _count == _data.Length ? _data : ToByteArray();
            }

            #endregion

            #region LastIndexOf (read-only)

            public override int LastIndexOf(char c, int start, int count) {
                return SwitchToChars().LastIndexOf(c, start, count);
            }

            public override int LastIndexOf(byte b, int start, int count) {
                Utils.NormalizeLastIndexOfIndices(_count, ref start, ref count);
                return Array.LastIndexOf(_data, b, start, count);
            }

            public override int LastIndexOf(string/*!*/ str, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.LastIndexOf(_data, _count, str, start, count);
                } else {
                    return SwitchToChars().LastIndexOf(str, start, count);
                }
            }

            public override int LastIndexOf(byte[]/*!*/ bytes, int start, int count) {
                // Array.LastIndexOf has different semantics for indices than String.LastIndexOf:
                return Utils.LastIndexOf(_data, _count, bytes, start, count);
            }

            public override int LastIndexIn(Content/*!*/ str, int start, int count) {
                return str.LastIndexOf(ExactData(), start, count);
            }

            #endregion

            #region Concatenate (read-only)

            public override Content/*!*/ Concat(Content/*!*/ content) {
                return content.ConcatTo(this);
            }

            internal BinaryContent/*!*/ Concatenate(CharArrayContent/*!*/ content) {
                int count = content.GetDataByteCount();
                var result = new byte[_count + count];
                Array.Copy(_data, 0, result, 0, _count);
                content.GetDataBytes(result, _count);
                return Create(result, null);
            }

            internal BinaryContent/*!*/ Concatenate(StringContent/*!*/ content) {
                int count = content.GetDataByteCount();
                var result = new byte[_count + count];
                Array.Copy(_data, 0, result, 0, _count);
                content.GetDataBytes(result, _count);
                return Create(result, null);
            }

            // binary + binary(self) -> binary
            public override Content/*!*/ ConcatTo(BinaryContent/*!*/ content) {
                return Create(Utils.Concatenate(content._data, content._count, _data, _count), null);
            }

            // chars + binary(self) -> binary
            public override Content/*!*/ ConcatTo(CharArrayContent/*!*/ content) {
                int count = content.GetDataByteCount();
                var result = new byte[count + _count];
                content.GetDataBytes(result, 0);
                Array.Copy(_data, 0, result, count, _count);
                return Create(result, null);
            }

            // string + binary(self) -> binary
            public override Content/*!*/ ConcatTo(StringContent/*!*/ content) {
                int count = content.GetDataByteCount();
                var result = new byte[count + _count];
                content.GetDataBytes(result, 0);
                Array.Copy(_data, 0, result, count, _count);
                return Create(result, null);
            }

            #endregion

            #region Append

            public override void Append(char c, int repeatCount) {
                if (_owner.HasByteCharacters || c < 0x80 && _owner._encoding.IsAsciiIdentity) {
                    Append((byte)c, repeatCount);
                } else {
                    _count = Utils.Append(ref _data, _count, c, repeatCount, _owner._encoding.EscapingEncoding);
                }
            }

            public override void Append(byte b, int repeatCount) {
                _count = Utils.Append(ref _data, _count, b, repeatCount);
            }

            public override void Append(string/*!*/ str, int start, int count) {
                AppendBytes(str, start, count);
            }

            public override void Append(char[]/*!*/ chars, int start, int count) {
                AppendBytes(chars, start, count);
            }

            public override void Append(byte[]/*!*/ bytes, int start, int count) {
                _count = Utils.Append(ref _data, _count, bytes, start, count);
            }

            public override void Append(Stream/*!*/ stream, int count) {
                Utils.Resize(ref _data, _count + count);
                _count += stream.Read(_data, _count, count);
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
                if (start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }
                
                content.Append(_data, start, count);
            }

            // content.chars + this.bytes[start, count]
            public override void AppendTo(CharArrayContent/*!*/ content, int start, int count) {
                if (start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.SwitchToBinary().Append(_data, start, count);
            }

            // content.chars + this.bytes[start, count]
            public override void AppendTo(StringContent/*!*/ content, int start, int count) {
                if (start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.SwitchToBinary().Append(_data, start, count);
            }

            #endregion

            #region Insert

            public override void Insert(int index, char c) {
                if (_owner.HasByteCharacters || c < 0x80 && _owner._encoding.IsAsciiIdentity) {
                    _count = Utils.InsertAt(ref _data, _count, index, (byte)c, 1);
                } else {
                    SwitchToChars(1).Insert(index, c);
                }
            }

            public override void Insert(int index, byte b) {
                _count = Utils.InsertAt(ref _data, _count, index, b, 1);
            }

            public override void Insert(int index, string/*!*/ str, int start, int count) {
                SwitchToChars(count).Insert(index, str, start, count);
            }

            public override void Insert(int index, char[]/*!*/ chars, int start, int count) {
                SwitchToChars(count).Insert(index, chars, start, count);
            }

            public override void Insert(int index, byte[]/*!*/ bytes, int start, int count) {
                _count = Utils.InsertAt(ref _data, _count, index, bytes, start, count);
            }

            public override void InsertTo(Content/*!*/ str, int index, int start, int count) {
                str.Insert(index, _data, start, count);
            }

            public override void SetByte(int index, byte b) {
                if (index >= _count) {
                    throw new IndexOutOfRangeException();
                }
                _data[index] = b;
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
                _count = Utils.Remove(ref _data, _count, start, count);
            }

            public override void Write(int offset, byte[]/*!*/ value, int start, int count) {
                Utils.Resize(ref _data, offset + count);
                _count = Math.Max(_count, offset + count);
                Buffer.BlockCopy(value, start, _data, offset, count);
            }

            public override void Write(int offset, byte value, int repeatCount) {
                int end = offset + repeatCount;
                Utils.Resize(ref _data, end);
                if (end > _count) {
                    _count = end;
                }
                for (int i = offset; i < end; i++) {
                    _data[i] = value;
                }
            }

            #endregion
        }
    }
}

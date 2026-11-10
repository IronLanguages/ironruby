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
using IronRuby.Runtime;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace IronRuby.Builtins {
    public partial class MutableString {
        /// <summary>
        /// Mutable character array. 
        /// All indices and counts are in characters. Surrogate pairs are treated as 2 separate characters.
        /// </summary>
        [Serializable]
        internal sealed class CharArrayContent : Content {
            private char[]/*!*/ _data;
            private int _count;
            private string _immutableSnapshot; // TODO: weak ref?

            // True while _data may be referenced by another content as well: see BinaryContent._shared.
            private bool _shared;

            // MutableString.CharIndex.cs: the character index of this content, when it holds a
            // surrogate pair and has been indexed; valid while the owner has no
            // CharIndexChangedFlags. Kept here rather than in a weak table, which costs far more to
            // update, since mutable content is exactly what gets its table rebuilt.
            internal object CharIndexTable;

            internal CharArrayContent(char[]/*!*/ data, MutableString owner)
                : this(data, data.Length, owner) {
            }

            internal CharArrayContent(char[]/*!*/ data, int count, MutableString owner) 
                : base(owner) {
                Assert.NotNull(data);
                Debug.Assert(count >= 0 && count <= data.Length);
                _data = data;
                _count = count;
            }

            // A content that shares data with another: see _shared.
            private CharArrayContent(char[]/*!*/ data, int count, MutableString owner, string immutableSnapshot)
                : this(data, count, owner) {
                _shared = true;
                _immutableSnapshot = immutableSnapshot;
            }

            /// <summary>
            /// Gives this content an array of its own if it shares one, before a write, with room for at
            /// least <paramref name="minCapacity"/> characters so that an append does not copy a second time.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void Unshare(int minCapacity) {
                if (_shared) {
                    UnshareSlow(minCapacity);
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void UnshareSlow(int minCapacity) {
                // Room past the end is zeroed, as a fresh array's would be; the rest is copied over.
                var data = Utils.NewArrayToFill<char>(Math.Max(_count, minCapacity));
                Array.Copy(_data, 0, data, 0, _count);
                Array.Clear(data, _count, data.Length - _count);
                _data = data;
                _shared = false;
            }

            internal override void PrepareForWrite(int minCapacity) {
                Unshare(minCapacity);
            }

            internal ReadOnlySpan<char> GetDataSpan() {
                return new ReadOnlySpan<char>(_data, 0, _count);
            }

            internal BinaryContent/*!*/ SwitchToBinary() {
                return SwitchToBinary(0);
            }

            private BinaryContent/*!*/ SwitchToBinary(int additionalCapacity) {
                var bytes = DataToBytes(additionalCapacity);
                return WrapContent(bytes, bytes.Length - additionalCapacity);
            }

            private byte[]/*!*/ DataToBytes(int additionalCapacity) {
                if (_count == 0) {
                    return (additionalCapacity == 0) ? Utils.EmptyBytes : new byte[additionalCapacity];
                } else if (additionalCapacity == 0) {
                    return _owner._encoding.EscapingEncoding.GetBytes(_data, 0, _count);
                } else {
                    var result = new byte[GetDataByteCount() + additionalCapacity];
                    GetDataBytes(result, 0);
                    return result;
                }
            }

            internal int GetDataByteCount() {
                return _owner._encoding.EscapingEncoding.GetByteCount(_data, 0, _count);
            }

            internal void GetDataBytes(byte[]/*!*/ bytes, int start) {
                _owner._encoding.EscapingEncoding.GetBytes(_data, 0, _count, bytes, start);
            }

            public void DataSetChar(int index, char c) {
                Debug.Assert(index < _count);
                Unshare(0);
                _data[index] = c;
            }

            #region UpdateCharacterFlags, CalculateHashCode, Length, Clone, Count (read-only)

            public override uint UpdateCharacterFlags(uint flags) {
                return UpdateAsciiAndSurrogatesFlags(_data, _count, flags);
            }

            // The same hash as ConvertToString().GetHashCode() (String.GetHashCode of a span is defined
            // to be the hash of a string with those characters), without making the string.
            public override int CalculateHashCode() {
                return String.GetHashCode(new ReadOnlySpan<char>(_data, 0, _count));
            }

            public override int Count {
                get { return _count; }
                set {
                    Unshare(value);
                    if (_data.Length < value) {
                        Array.Resize(ref _data, Utils.GetExpandedSize(_data, value));
                    } else {
                        Utils.Fill(_data, _count, '\0', value - _count);
                    }
                    _count = value;
                }
            }

            public override bool IsEmpty {
                get { return _count == 0; }
            }

            public override int GetCharCount() {
                return _count;
            }

            public override int GetCharacterCount() {
                return _owner.HasSurrogates() ? _data.GetCharacterCount(_count) : _count;
            }

            public override int GetByteCount() {
                return (_owner.HasSingleByteCharacters || _count == 0) ? _count : SwitchToBinary().GetByteCount();
            }

            public override Content/*!*/ SwitchToBinaryContent() {
                return SwitchToBinary();
            }

            public override Content/*!*/ SwitchToStringContent() {
                return this;
            }

            public override Content/*!*/ SwitchToMutableContent() {
                return this;
            }

            // Shares the array (see _shared), and the CLR string of it if one has been made and is
            // still up to date.
            public override Content/*!*/ Clone() {
                _shared = true;
                string snapshot = (_immutableSnapshot != null && !_owner.IsFlagSet(MutableString.HasChangedCharArrayToStringFlag))
                    ? _immutableSnapshot : null;
                return new CharArrayContent(_data, _count, _owner, snapshot);
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
                if (_immutableSnapshot == null || _owner.IsFlagSet(MutableString.HasChangedCharArrayToStringFlag)) {
                    _immutableSnapshot = GetStringSlice(0, _count);
                    _owner.ClearFlag(MutableString.HasChangedCharArrayToStringFlag);
                }
                return _immutableSnapshot;
            }

            public override byte[]/*!*/ ConvertToBytes() {
                var binary = SwitchToBinary();
                return binary.GetBinarySlice(0, binary.GetByteCount());
            }

            public override string/*!*/ ToString() {
                return new String(_data, 0, _count);
            }

            public override byte[]/*!*/ ToByteArray() {
                return DataToBytes(0);
            }

            internal override byte[]/*!*/ GetByteArray(out int count) {
                return SwitchToBinary().GetByteArray(out count);
            }

            public override Content/*!*/ EscapeRegularExpression() {
                // TODO:
                StringBuilder sb = RubyRegex.EscapeToStringBuilder(ToString());
                return (sb != null) ? new CharArrayContent(sb.ToString().ToCharArray(), _owner) : Clone();
            }

            public override void CheckEncoding() {
                _owner._encoding.StrictEncoding.GetByteCount(_data, 0, _count);
            }

            public override bool ContainsInvalidCharacters() {
                return Utils.ContainsInvalidCharacters(_data, 0, _count, _owner._encoding.StrictEncoding);
            }

            #endregion

            #region CompareTo (read-only)

            public override int OrdinalCompareTo(string/*!*/ str) {
                return _data.ValueCompareTo(_count, str);
            }

            internal int OrdinalCompareTo(char[]/*!*/ chars, int count) {
                return _data.ValueCompareTo(_count, chars, count);
            }

            // this <=> content
            public override int OrdinalCompareTo(Content/*!*/ content) {
                return content.ReverseOrdinalCompareTo(this);
            }

            // content.bytes <=> this.chars
            public override int ReverseOrdinalCompareTo(BinaryContent/*!*/ content) {
                return SwitchToBinary().ReverseOrdinalCompareTo(content);
            }

            // content.chars <=> this.chars
            public override int ReverseOrdinalCompareTo(CharArrayContent/*!*/ content) {
                return content.OrdinalCompareTo(_data, _count);
            }

            // content.chars <=> this.chars
            public override int ReverseOrdinalCompareTo(StringContent/*!*/ content) {
                return content.OrdinalCompareTo(_data, _count);
            }

            #endregion

            #region Slices (read-only)

            public override char GetChar(int index) {
                if (index >= _count) {
                    throw new IndexOutOfRangeException();
                }
                return _data[index];
            }

            public override byte GetByte(int index) {
                if (index == 0 || _owner.HasSingleByteCharacters && !_owner.HasSurrogates()) {
                    if (index >= _count) {
                        throw new IndexOutOfRangeException();
                    }
                    var result = _data[index];
                    if (result < 0x80 || _owner.HasByteCharacters) {
                        return (byte)_data[index];
                    }
                }
                return SwitchToBinary().GetByte(index);
            }

            public override string/*!*/ GetStringSlice(int start, int count) {
                return _data.GetStringSlice(_count, start, count);
            }

            public override byte[]/*!*/ GetBinarySlice(int start, int count) {
                if (_owner.HasByteCharacters && !_owner.HasSurrogates() && start >= 0 && count >= 0 && count <= _count - start) {
                    // Every character is its own byte, so the slice can be taken without switching
                    // the whole string over to a byte array. Switching is what makes a mixed
                    // reader - StringScanner matches on the characters and then slices out the
                    // bytes it matched - convert the entire subject back and forth on every token.
                    var result = new byte[count];
                    for (int i = 0; i < count; i++) {
                        result[i] = (byte)_data[start + i];
                    }
                    return result;
                }
                return SwitchToBinary().GetBinarySlice(start, count);
            }

            public override Content/*!*/ GetSlice(int start, int count) {
                if (start == 0 && Utils.IsSharedPrefix(_data.Length, count = Math.Min(count, _count))) {
                    // see Utils.IsSharedPrefix
                    _shared = true;
                    return new CharArrayContent(_data, count, _owner, null);
                }
                return new CharArrayContent(_data.GetSlice(_count, start, count), _owner);
            }

            public override CharacterEnumerator/*!*/ GetCharacters() {
                return new MutableString.CharArrayCharacterEnumerator(_owner.Encoding, _data, _count);
            }

            public override IEnumerable<byte>/*!*/ GetBytes() {
                if (_owner.HasByteCharacters) {
                    return Utils.EnumerateAsBytes(_data, _count);
                } else {
                    return SwitchToBinary().GetBytes();
                }
            }

            #endregion

            #region StartsWith (read-only)

            public override bool StartsWith(char c) {
                return _count != 0 && _data[0] == c;
            }

            #endregion

            #region IndexOf (read-only)

            //
            // Searching for Unicode characters/strings (doesn't work correctly in Ruby 1.9.1):
            //
            // å == U+00E5 == (U+0061, U+030A)
            // string str = "combining mark: a\u030a";
            // Console.WriteLine(str.IndexOf("å")); // 16
            // Console.WriteLine(str.IndexOf('å')); // -1       
            //

            public override int IndexOf(char c, int start, int count) {
                count = Utils.NormalizeCount(_count, start, count);
                return count > 0 ? Array.IndexOf(_data, c, start, count) : -1;
            }

            public override int IndexOf(byte b, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.IndexOf(_data, _count, b, start, count);
                } else {
                    return SwitchToBinary().IndexOf(b, start, count);
                }
            }

            public override int IndexOf(string/*!*/ str, int start, int count) {
                return Utils.IndexOf(_data, _count, str, start, count);
            }

            public override int IndexOf(byte[]/*!*/ bytes, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.IndexOf(_data, _count, bytes, start, count);
                } else {
                    return SwitchToBinary().IndexOf(bytes, start, count);
                }
                
            }

            public override int IndexIn(Content/*!*/ str, int start, int count) {
                return str.IndexOf(ToString(), start, count);
            }

            #endregion

            #region LastIndexOf (read-only)

            public override int LastIndexOf(char c, int start, int count) {
                Utils.NormalizeLastIndexOfIndices(_count, ref start, ref count);
                return Array.LastIndexOf(_data, c, start, count);
            }

            public override int LastIndexOf(byte b, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.LastIndexOf(_data, _count, b, start, count);
                } else {
                    return SwitchToBinary().LastIndexOf(b, start, count);
                }
            }

            public override int LastIndexOf(string/*!*/ str, int start, int count) {
                return Utils.LastIndexOf(_data, _count, str, start, count);
            }

            public override int LastIndexOf(byte[]/*!*/ bytes, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.LastIndexOf(_data, _count, bytes, start, count);
                } else {
                    return SwitchToBinary().LastIndexOf(bytes, start, count);
                }
            }

            public override int LastIndexIn(Content/*!*/ str, int start, int count) {
                return str.LastIndexOf(ToString(), start, count);
            }

            #endregion

            #region Concatenate (read-only)

            public override Content/*!*/ Concat(Content/*!*/ content) {
                return content.ConcatTo(this);
            }

            internal CharArrayContent/*!*/ Concatenate(StringContent/*!*/ content) {
                int count = content.Data.Length;
                var result = Utils.NewArrayToFill<char>(_count + count);
                Array.Copy(_data, 0, result, 0, _count);
                content.Data.CopyTo(0, result, _count, count);
                return new CharArrayContent(result, null);
            }

            // binary + chars(self) -> binary
            public override Content/*!*/ ConcatTo(BinaryContent/*!*/ content) {
                return content.Concatenate(this);
            }

            // chars + chars(self) -> chars
            public override Content/*!*/ ConcatTo(CharArrayContent/*!*/ content) {
                return new CharArrayContent(Utils.Concatenate(content._data, content._count, _data, _count), null);
            }

            // string + chars(self) -> chars
            public override Content/*!*/ ConcatTo(StringContent/*!*/ content) {
                int count = content.Data.Length;
                var result = Utils.NewArrayToFill<char>(count + _count);
                content.Data.CopyTo(0, result, 0, count);
                Array.Copy(_data, 0, result, count, _count);
                return new CharArrayContent(result, null);
            }

            #endregion

            #region Append

            public override void Append(char c, int repeatCount) {
                Unshare(_count + repeatCount);
                _count = Utils.Append(ref _data, _count, c, repeatCount);
            }

            public override void Append(byte b, int repeatCount) {
                SwitchToBinary(repeatCount).Append(b, repeatCount);
            }

            public override void Append(string/*!*/ str, int start, int count) {
                Unshare(_count + count);
                _count = Utils.Append(ref _data, _count, str, start, count);
            }

            public override void Append(char[]/*!*/ chars, int start, int count) {
                Unshare(_count + count);
                _count = Utils.Append(ref _data, _count, chars, start, count);
            }

            public override void Append(byte[]/*!*/ bytes, int start, int count) {
                SwitchToBinary(count).Append(bytes, start, count);
            }

            public override void Append(Stream/*!*/ stream, int count) {
                SwitchToBinary(count).Append(stream, count);
            }

            public override void AppendFormat(IFormatProvider provider, string/*!*/ format, object[]/*!*/ args) {
                var formatted = String.Format(provider, format, args);
                Append(formatted, 0, formatted.Length);
            }

            // this + content[start, count]
            public override void Append(Content/*!*/ content, int start, int count) {
                content.AppendTo(this, start, count);
            }

            // content.bytes + this.chars[start, count]
            public override void AppendTo(BinaryContent/*!*/ content, int start, int count) {
                if (start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.AppendBytes(_data, start, count);
            }

            // content.chars + this.chars[start, count]
            public override void AppendTo(CharArrayContent/*!*/ content, int start, int count) {
                if (start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.Append(_data, start, count);
            }

            // content.chars + this.chars[start, count]
            public override void AppendTo(StringContent/*!*/ content, int start, int count) {
                if (start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.Append(_data, start, count);
            }

            #endregion

            #region Insert

            public override void Insert(int index, char c) {
                Unshare(_count + 1);
                _count = Utils.InsertAt(ref _data, _count, index, c, 1);
            }

            public override void Insert(int index, byte b) {
                if (b < 0x80 && _owner.HasByteCharacters) {
                    Insert(index, (char)b);
                } else {
                    SwitchToBinary(1).Insert(index, b);
                }
            }

            public override void Insert(int index, string/*!*/ str, int start, int count) {
                Unshare(_count + count);
                _count = Utils.InsertAt(ref _data, _count, index, str, start, count);
            }

            public override void Insert(int index, char[]/*!*/ chars, int start, int count) {
                Unshare(_count + count);
                _count = Utils.InsertAt(ref _data, _count, index, chars, start, count);
            }

            public override void Insert(int index, byte[]/*!*/ bytes, int start, int count) {
                SwitchToBinary(count).Insert(index, bytes, start, count);
            }

            public override void InsertTo(Content/*!*/ str, int index, int start, int count) {
                str.Insert(index, _data, start, count);
            }

            public override void SetByte(int index, byte b) {
                if (b < 0x80 && _owner.HasByteCharacters) {
                    DataSetChar(index, (char)b);
                } else {
                    SwitchToBinary().SetByte(index, b);
                }
            }

            public override void SetChar(int index, char c) {
                DataSetChar(index, c);
            }

            #endregion

            #region Remove, Write

            public override void Remove(int start, int count) {
                if (_shared) {
                    // see BinaryContent.Remove
                    _count = Utils.RemoveShared(ref _data, _count, start, count, ref _shared);
                    return;
                }
                _count = Utils.Remove(ref _data, _count, start, count);
            }

            public override void Write(int offset, byte[]/*!*/ value, int start, int count) {
                SwitchToBinary().Write(offset, value, start, count);
            }

            public override void Write(int offset, byte value, int repeatCount) {
                SwitchToBinary().Write(offset, value, repeatCount);
            }

            #endregion
        }
    }
}

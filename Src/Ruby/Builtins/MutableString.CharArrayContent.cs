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
            // The characters are _data[_start, _start + _count): see BinaryContent._start.
            private char[]/*!*/ _data;
            private int _start;
            private int _count;
            private string _immutableSnapshot; // TODO: weak ref?

            // Whether _data may be referenced by another content as well, and whether a thread holds
            // this content: see BinaryContent._state.
            private int _state;

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

            // A content that shares data with another: see _state.
            private CharArrayContent(char[]/*!*/ data, int start, int count, MutableString owner, string immutableSnapshot)
                : base(owner) {
                Debug.Assert(start >= 0 && count >= 0 && count <= data.Length - start);
                _data = data;
                _start = start;
                _count = count;
                _state = ContentState.Shared;
                _immutableSnapshot = immutableSnapshot;
            }

            /// <summary>
            /// Holds this content for a write until <see cref="ExitWrite"/>, and gives it an array of its
            /// own if it shares one, with room for at least <paramref name="minCapacity"/> characters so
            /// that an append does not copy a second time.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void EnterWrite(int minCapacity) {
                if (!ContentState.TryEnterUnshared(ref _state)) {
                    EnterWriteSlow(minCapacity);
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void EnterWriteSlow(int minCapacity) {
                if (ContentState.Enter(ref _state)) {
                    try {
                        // Room past the end is zeroed, as a fresh array's would be; the rest is copied over.
                        var data = Utils.NewArrayToFill<char>(Math.Max(_count, minCapacity));
                        Array.Copy(_data, _start, data, 0, _count);
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

            internal ReadOnlySpan<char> GetDataSpan() {
                return new ReadOnlySpan<char>(_data, _start, _count);
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
                    return _owner._encoding.EscapingEncoding.GetBytes(_data, _start, _count);
                } else {
                    var result = new byte[GetDataByteCount() + additionalCapacity];
                    GetDataBytes(result, 0);
                    return result;
                }
            }

            internal int GetDataByteCount() {
                return _owner._encoding.EscapingEncoding.GetByteCount(_data, _start, _count);
            }

            internal void GetDataBytes(byte[]/*!*/ bytes, int start) {
                _owner._encoding.EscapingEncoding.GetBytes(_data, _start, _count, bytes, start);
            }

            public void DataSetChar(int index, char c) {
                Debug.Assert(index < _count);
                EnterWrite(0);
                try {
                    _data[index] = c;
                } finally {
                    ExitWrite();
                }
            }

            #region UpdateCharacterFlags, CalculateHashCode, Length, Clone, Count (read-only)

            public override uint UpdateCharacterFlags(uint flags) {
                return UpdateAsciiAndSurrogatesFlags(_data, _start, _count, flags);
            }

            // See Content.HashCharacters.
            public override int CalculateHashCode() {
                return HashCharacters(GetDataSpan());
            }

            public override int Count {
                get { return _count; }
                set {
                    EnterWrite(value);
                    try {
                        if (_data.Length < value) {
                            Array.Resize(ref _data, Utils.GetExpandedSize(_data, value));
                        } else {
                            Utils.Fill(_data, _count, '\0', value - _count);
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
                return _count;
            }

            public override int GetCharacterCount() {
                return _owner.HasSurrogates() ? Utils.GetCharacterCount(GetDataSpan()) : _count;
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

            // Shares the array (see _state), and the CLR string of it if one has been made and is
            // still up to date.
            public override Content/*!*/ Clone() {
                ContentState.Enter(ref _state);
                try {
                    string snapshot = (_immutableSnapshot != null && !_owner.IsFlagSet(MutableString.HasChangedCharArrayToStringFlag))
                        ? _immutableSnapshot : null;
                    return new CharArrayContent(_data, _start, _count, _owner, snapshot);
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
                return new String(_data, _start, _count);
            }

            public override byte[]/*!*/ ToByteArray() {
                return DataToBytes(0);
            }

            internal override byte[]/*!*/ GetByteArray(out int start, out int count) {
                return SwitchToBinary().GetByteArray(out start, out count);
            }

            public override Content/*!*/ EscapeRegularExpression() {
                // TODO:
                StringBuilder sb = RubyRegex.EscapeToStringBuilder(ToString());
                return (sb != null) ? new CharArrayContent(sb.ToString().ToCharArray(), _owner) : Clone();
            }

            public override void CheckEncoding() {
                _owner._encoding.StrictEncoding.GetByteCount(_data, _start, _count);
            }

            public override bool ContainsInvalidCharacters() {
                return Utils.ContainsInvalidCharacters(GetDataSpan(), _owner._encoding.StrictEncoding);
            }

            #endregion

            #region CompareTo (read-only)

            public override int OrdinalCompareTo(string/*!*/ str) {
                return Utils.ValueCompareTo(GetDataSpan(), str.AsSpan());
            }

            internal int OrdinalCompareTo(ReadOnlySpan<char> chars) {
                return Utils.ValueCompareTo(GetDataSpan(), chars);
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
                return content.OrdinalCompareTo(GetDataSpan());
            }

            // content.chars <=> this.chars
            public override int ReverseOrdinalCompareTo(StringContent/*!*/ content) {
                return content.OrdinalCompareTo(GetDataSpan());
            }

            #endregion

            #region Slices (read-only)

            public override char GetChar(int index) {
                if ((uint)index >= (uint)_count) {
                    throw new IndexOutOfRangeException();
                }
                return _data[_start + index];
            }

            public override byte GetByte(int index) {
                if (index == 0 || _owner.HasSingleByteCharacters && !_owner.HasSurrogates()) {
                    if ((uint)index >= (uint)_count) {
                        throw new IndexOutOfRangeException();
                    }
                    var result = _data[_start + index];
                    if (result < 0x80 || _owner.HasByteCharacters) {
                        return (byte)result;
                    }
                }
                return SwitchToBinary().GetByte(index);
            }

            public override string/*!*/ GetStringSlice(int start, int count) {
                return Utils.GetStringSlice(GetDataSpan(), start, count);
            }

            public override byte[]/*!*/ GetBinarySlice(int start, int count) {
                if (_owner.HasByteCharacters && !_owner.HasSurrogates() && start >= 0 && count >= 0 && count <= _count - start) {
                    // Every character is its own byte, so the slice can be taken without switching
                    // the whole string over to a byte array. Switching is what makes a mixed
                    // reader - StringScanner matches on the characters and then slices out the
                    // bytes it matched - convert the entire subject back and forth on every token.
                    var result = new byte[count];
                    var chars = GetDataSpan().Slice(start, count);
                    for (int i = 0; i < chars.Length; i++) {
                        result[i] = (byte)chars[i];
                    }
                    return result;
                }
                return SwitchToBinary().GetBinarySlice(start, count);
            }

            public override Content/*!*/ GetSlice(int start, int count) {
                if (Utils.IsSharedSlice(_data.Length, Utils.NormalizeCount(_count, start, count))) {
                    // see Utils.IsSharedSlice; checked again while holding the content
                    bool shared = ContentState.Enter(ref _state);
                    try {
                        int sliceCount = Utils.NormalizeCount(_count, start, count);
                        if (Utils.IsSharedSlice(_data.Length, sliceCount)) {
                            shared = true;
                            return new CharArrayContent(_data, _start + start, sliceCount, _owner, null);
                        }
                    } finally {
                        ContentState.Exit(ref _state, shared);
                    }
                }
                return new CharArrayContent(Utils.GetSlice(GetDataSpan(), start, count), _owner);
            }

            public override CharacterEnumerator/*!*/ GetCharacters() {
                return new MutableString.CharArrayCharacterEnumerator(_owner.Encoding, _data, _start, _count);
            }

            public override IEnumerable<byte>/*!*/ GetBytes() {
                if (_owner.HasByteCharacters) {
                    return Utils.EnumerateAsBytes(_data, _start, _count);
                } else {
                    return SwitchToBinary().GetBytes();
                }
            }

            #endregion

            #region StartsWith (read-only)

            public override bool StartsWith(char c) {
                return _count != 0 && _data[_start] == c;
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
                if (count <= 0) {
                    return -1;
                }
                int i = GetDataSpan().Slice(start, count).IndexOf(c);
                return i < 0 ? -1 : start + i;
            }

            public override int IndexOf(byte b, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.IndexOf(GetDataSpan(), b, start, count);
                } else {
                    return SwitchToBinary().IndexOf(b, start, count);
                }
            }

            public override int IndexOf(string/*!*/ str, int start, int count) {
                return Utils.IndexOf(GetDataSpan(), str, start, count);
            }

            public override int IndexOf(byte[]/*!*/ bytes, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.IndexOf(GetDataSpan(), bytes, start, count);
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
                if (count <= 0) {
                    return -1;
                }
                int finish = start - count + 1;
                int i = GetDataSpan().Slice(finish, count).LastIndexOf(c);
                return i < 0 ? -1 : finish + i;
            }

            public override int LastIndexOf(byte b, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.LastIndexOf(GetDataSpan(), b, start, count);
                } else {
                    return SwitchToBinary().LastIndexOf(b, start, count);
                }
            }

            public override int LastIndexOf(string/*!*/ str, int start, int count) {
                return Utils.LastIndexOf(GetDataSpan(), str, start, count);
            }

            public override int LastIndexOf(byte[]/*!*/ bytes, int start, int count) {
                if (_owner.HasByteCharacters) {
                    return Utils.LastIndexOf(GetDataSpan(), bytes, start, count);
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
                GetDataSpan().CopyTo(result);
                content.Data.CopyTo(0, result, _count, count);
                return new CharArrayContent(result, null);
            }

            // binary + chars(self) -> binary
            public override Content/*!*/ ConcatTo(BinaryContent/*!*/ content) {
                return content.Concatenate(this);
            }

            // chars + chars(self) -> chars
            public override Content/*!*/ ConcatTo(CharArrayContent/*!*/ content) {
                var result = Utils.NewArrayToFill<char>(content._count + _count);
                content.GetDataSpan().CopyTo(result);
                GetDataSpan().CopyTo(result.AsSpan(content._count));
                return new CharArrayContent(result, null);
            }

            // string + chars(self) -> chars
            public override Content/*!*/ ConcatTo(StringContent/*!*/ content) {
                int count = content.Data.Length;
                var result = Utils.NewArrayToFill<char>(count + _count);
                content.Data.CopyTo(0, result, 0, count);
                GetDataSpan().CopyTo(result.AsSpan(count));
                return new CharArrayContent(result, null);
            }

            #endregion

            #region Append

            public override void Append(char c, int repeatCount) {
                EnterWrite(_count + repeatCount);
                try {
                    _count = Utils.Append(ref _data, _count, c, repeatCount);
                } finally {
                    ExitWrite();
                }
            }

            public override void Append(byte b, int repeatCount) {
                SwitchToBinary(repeatCount).Append(b, repeatCount);
            }

            public override void Append(string/*!*/ str, int start, int count) {
                EnterWrite(_count + count);
                try {
                    _count = Utils.Append(ref _data, _count, str, start, count);
                } finally {
                    ExitWrite();
                }
            }

            public override void Append(char[]/*!*/ chars, int start, int count) {
                EnterWrite(_count + count);
                try {
                    _count = Utils.Append(ref _data, _count, chars, start, count);
                } finally {
                    ExitWrite();
                }
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
                if (start < 0 || start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.AppendBytes(_data, _start + start, count);
            }

            // content.chars + this.chars[start, count]
            public override void AppendTo(CharArrayContent/*!*/ content, int start, int count) {
                if (start < 0 || start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.Append(_data, _start + start, count);
            }

            // content.chars + this.chars[start, count]
            public override void AppendTo(StringContent/*!*/ content, int start, int count) {
                if (start < 0 || start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }

                content.Append(_data, _start + start, count);
            }

            #endregion

            #region Insert

            public override void Insert(int index, char c) {
                EnterWrite(_count + 1);
                try {
                    _count = Utils.InsertAt(ref _data, _count, index, c, 1);
                } finally {
                    ExitWrite();
                }
            }

            public override void Insert(int index, byte b) {
                if (b < 0x80 && _owner.HasByteCharacters) {
                    Insert(index, (char)b);
                } else {
                    SwitchToBinary(1).Insert(index, b);
                }
            }

            public override void Insert(int index, string/*!*/ str, int start, int count) {
                EnterWrite(_count + count);
                try {
                    _count = Utils.InsertAt(ref _data, _count, index, str, start, count);
                } finally {
                    ExitWrite();
                }
            }

            public override void Insert(int index, char[]/*!*/ chars, int start, int count) {
                EnterWrite(_count + count);
                try {
                    _count = Utils.InsertAt(ref _data, _count, index, chars, start, count);
                } finally {
                    ExitWrite();
                }
            }

            public override void Insert(int index, byte[]/*!*/ bytes, int start, int count) {
                SwitchToBinary(count).Insert(index, bytes, start, count);
            }

            public override void InsertTo(Content/*!*/ str, int index, int start, int count) {
                if (start < 0 || count < 0 || start > _count - count) {
                    throw new ArgumentOutOfRangeException("start");
                }
                str.Insert(index, _data, _start + start, count);
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
                bool shared = ContentState.Enter(ref _state);
                try {
                    if (shared) {
                        // see BinaryContent.Remove
                        _count = Utils.RemoveShared(ref _data, ref _start, _count, start, count, ref shared);
                    } else {
                        _count = Utils.Remove(ref _data, _count, start, count);
                    }
                } finally {
                    ContentState.Exit(ref _state, shared);
                }
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

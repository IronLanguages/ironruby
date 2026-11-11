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
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace IronRuby.Builtins {
    public partial class MutableString {
        // TODO: Add range checks to APIs or change the implementation to be out-of-range tolerant (see GetSlice).
        // Tha latter is preferred if the semantics is reasonable since it reduces the amounts of content size dependent checks that the user needs to do.
        /// <summary>
        /// The state word of a BinaryContent or CharArrayContent: whether its array may be shared with
        /// another content (copy-on-write, see Content.Clone), and whether a thread holds the content.
        /// There is no GVL, so a #dup can run while another thread writes the string, and the copy
        /// must not change after the dup returns (a frozen copy may be a Hash key). Clone, a sharing
        /// GetSlice and every write hold the content, so a write cannot check that the array is its
        /// own, let a Clone hand the array out, and then write into it; and a copy made while the
        /// array is replaced cannot see the new array with the old count or lose its shared mark.
        /// The common write - unshared, nobody holding it - costs one compare-exchange. Holding is not
        /// reentrant and other threads spin while it lasts, so nothing runs while a content is held but
        /// array copies and IronRuby's own encoders: no stream, no user code, nothing that could reach
        /// this string again (Append(Stream) reads before it holds the content).
        /// </summary>
        internal static class ContentState {
            internal const int Shared = 1;
            internal const int Held = 2;

            /// <summary>Holds the content if its array is its own and nobody holds it.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static bool TryEnterUnshared(ref int state) {
                return Interlocked.CompareExchange(ref state, Held, 0) == 0;
            }

            /// <summary>Holds the content, waiting while another thread does. Returns whether its array is shared.</summary>
            internal static bool Enter(ref int state) {
                var spin = new SpinWait();
                while (true) {
                    int s = Volatile.Read(ref state);
                    if ((s & Held) == 0 && Interlocked.CompareExchange(ref state, s | Held, s) == s) {
                        return (s & Shared) != 0;
                    }
                    spin.SpinOnce();
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Exit(ref int state, bool shared) {
                Volatile.Write(ref state, shared ? Shared : 0);
            }
        }

        [Serializable]
        internal abstract class Content {
            protected MutableString _owner;

            #region Utils

            internal void SetOwner(MutableString/*!*/ owner) {
                Assert.NotNull(owner);
                Debug.Assert(_owner == null || _owner.Encoding == owner.Encoding);
                _owner = owner;
            }

            protected Content(MutableString owner) {
                _owner = owner;
            }

            /// <summary>
            /// Whether this content is a slice from an offset into a shared array (GetSlice). Such a
            /// content never changes: readers take no lock, so its array and offset, two fields, must
            /// not change under them. The string replaces it with <see cref="Unslice"/> before its first
            /// write (MutableString.FrozenOrCopyOnWrite and the raw-array accessors).
            /// </summary>
            internal bool IsOffsetSlice;

            /// <summary>
            /// A content of this one's items in an array of their own, from index 0. Only for an
            /// <see cref="IsOffsetSlice"/> content; any other is returned as it is.
            /// </summary>
            internal Content/*!*/ Unslice() {
                return Unslice(0);
            }

            /// <summary>
            /// <see cref="Unslice()"/>, with room for <paramref name="additionalCapacity"/> more items past
            /// the copy, so that the append that is about to follow does not copy it a second time.
            /// </summary>
            internal virtual Content/*!*/ Unslice(int additionalCapacity) {
                return this;
            }

            protected BinaryContent/*!*/ WrapContent(byte[]/*!*/ bytes, int count) {
                BinaryContent result = new BinaryContent(bytes, count, _owner);
                _owner.SetContent(result);
                return result;
            }
            
            protected CharArrayContent/*!*/ WrapContent(char[]/*!*/ chars, int count) {
                var result = new CharArrayContent(chars, count, _owner);
                _owner.SetContent(result);
                return result;
            }

            protected StringContent/*!*/ WrapContent(string/*!*/ str) {
                var result = new StringContent(str, _owner);
                _owner.SetContent(result);
                return result;
            }

            /// <summary>
            /// The length from which a string whose characters are all ASCII hashes them as bytes
            /// (<see cref="HashAsciiBytes"/>) rather than as UTF-16 characters, whatever its encoding. A shorter one costs about the same either way, and
            /// keeps the hash it had - String.GetHashCode of its characters - with no narrowing first.
            /// ASCII characters and bytes are as many, so the choice does not depend on how the
            /// string is held.
            /// </summary>
            internal const int AsciiByteHashMinLength = 128;

            /// <summary>
            /// The hash of a long string whose characters are all ASCII, from those characters as bytes
            /// (in an encoding that <see cref="RubyEncoding.AsciiRoundTrips"/>, the string's own
            /// bytes; in any other, its characters narrowed): SipHash-1-3 under a random per-process
            /// key (<see cref="SipHash"/>), as CRuby hashes strings. That reads half the bytes the hash
            /// of the UTF-16 characters did, and SipHash-1-3 is about 1.5 times as fast per byte as
            /// Marvin, String.GetHashCode's seeded hash, which every other string still uses. Every
            /// equal string - bytes or characters, US-ASCII, UTF-8 or BINARY, each #eql? the others,
            /// and a CLR string #eql? to any of them - hashes the same.
            /// </summary>
            internal static int HashAsciiBytes(ReadOnlySpan<byte> ascii) {
                Debug.Assert(ascii.Length >= AsciiByteHashMinLength);
                return SipHash.Hash32(ascii);
            }

            /// <summary>
            /// The hash of a string held as characters (see <see cref="HashAsciiBytes"/>). The
            /// characters of a short string, or of one that is not all ASCII, are hashed as they are,
            /// which is what the bytes of one decode to (BinaryContent.CalculateHashCode).
            /// </summary>
            internal int HashCharacters(ReadOnlySpan<char> chars) {
                if (chars.Length < AsciiByteHashMinLength) {
                    return String.GetHashCode(chars);
                }
                // The hash depends on the characters alone, not on the encoding (a CLR string is #eql? to
                // a String of the same characters in any encoding) nor on the cached ASCII flags, which
                // a write in place can leave behind: the narrowing checks the characters themselves
                // and stops at the first that is not ASCII.
                return HashCharacters(chars, true);
            }

            [SkipLocalsInit]
            internal static int HashCharacters(ReadOnlySpan<char> chars, bool asciiAsBytes) {
                if (asciiAsBytes && chars.Length >= AsciiByteHashMinLength) {
                    byte[] rented = null;
                    Span<byte> bytes = (chars.Length <= 512) ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(chars.Length));
                    try {
                        if (System.Text.Ascii.FromUtf16(chars, bytes, out int written) == OperationStatus.Done) {
                            return HashAsciiBytes(bytes.Slice(0, written));
                        }
                    } finally {
                        if (rented != null) {
                            ArrayPool<byte>.Shared.Return(rented);
                        }
                    }
                }
                return String.GetHashCode(chars);
            }

            internal static uint UpdateAsciiAndSurrogatesFlags(string/*!*/ str, uint flags) {
                return UpdateAsciiAndSurrogatesFlags(str.AsSpan(), flags);
            }

            internal static uint UpdateAsciiAndSurrogatesFlags(char[]/*!*/ str, int start, int itemCount, uint flags) {
                return UpdateAsciiAndSurrogatesFlags(new ReadOnlySpan<char>(str, start, itemCount), flags);
            }

            // Vectorized: these scans run whenever the cached flags are out of date (after most mutations).
            private static uint UpdateAsciiAndSurrogatesFlags(ReadOnlySpan<char> str, uint flags) {
                if (str.IndexOfAnyInRange('\uD800', '\uDFFF') >= 0) {
                    return flags & ~(MutableString.SurrogatesUnknownFlag | MutableString.AsciiUnknownFlag | MutableString.IsAsciiFlag | MutableString.NoSurrogatesFlag);
                }

                return System.Text.Ascii.IsValid(str)
                  ? flags & ~(MutableString.SurrogatesUnknownFlag | MutableString.AsciiUnknownFlag) | MutableString.IsAsciiFlag | MutableString.NoSurrogatesFlag
                  : flags & ~(MutableString.SurrogatesUnknownFlag | MutableString.AsciiUnknownFlag | MutableString.IsAsciiFlag) | MutableString.NoSurrogatesFlag;
            }

            #endregion

            public abstract string/*!*/ ConvertToString();
            public abstract byte[]/*!*/ ConvertToBytes();
            public abstract Content/*!*/ SwitchToBinaryContent();
            public abstract Content/*!*/ SwitchToStringContent();
            public abstract Content/*!*/ SwitchToMutableContent();
            public abstract void CheckEncoding();
            public abstract bool ContainsInvalidCharacters();

            public abstract byte[]/*!*/ ToByteArray();
            /// <summary>
            /// The bytes in place: <paramref name="count"/> items of the array from <paramref name="start"/>,
            /// which is 0 unless the array is shared with another string (a slice, see GetSlice).
            /// Only for reading; see <see cref="Clone"/>.
            /// </summary>
            internal abstract byte[]/*!*/ GetByteArray(out int start, out int count);

            // returns self if there are no characters to be escaped:
            public abstract Content/*!*/ EscapeRegularExpression();

            // read:
            public abstract bool IsEmpty { get; }
            public abstract int Count { get; set; }
            public abstract int CalculateHashCode();
            public abstract uint UpdateCharacterFlags(uint flags);

            /// <summary>
            /// Returns the number of characters in the string.
            /// Counts surrogates as two characters.
            /// </summary>
            /// <exception cref="DecoderFallbackException">Invalid character.</exception>
            public abstract int GetCharCount();

            /// <summary>
            /// Returns the number of true Unicode characters in the string.
            /// </summary>
            public abstract int GetCharacterCount();

            /// <summary>
            /// Returns the number of bytes in the string.
            /// Throws if the string includes invalid characters.
            /// </summary>
            public abstract int GetByteCount();

            public abstract void TrimExcess();
            public abstract int GetCapacity();
            public abstract void SetCapacity(int capacity);
            /// <summary>
            /// A copy of the content, owned by the same string until it is given to another one.
            /// BinaryContent and CharArrayContent share their array with the copy instead of copying
            /// it (copy-on-write): both are marked shared, and each copies the array the first time it
            /// writes to it. That is what makes #dup, #clone, String.new(str) and a whole-buffer
            /// StringIO#read of a long string cost the same as of a short one, as they do in MRI.
            /// Every method that writes into the array of either class must hold the content with its
            /// EnterWrite/ExitWrite (see <see cref="ContentState"/>); anything outside them that writes
            /// into an array it got from GetByteArray must call <see cref="PrepareForWrite"/> before
            /// taking it. A slice (GetSlice) can share the array too, from an offset: a content's
            /// items need not start at index 0 of its array until it is written to, as EnterWrite
            /// copies them to an array of their own at index 0. StringContent wraps an immutable CLR string, so its copy shares that string
            /// with nothing to mark.
            /// </summary>
            public abstract Content/*!*/ Clone();

            /// <summary>
            /// Makes sure the array this content holds is its own (see <see cref="Clone"/>), with room
            /// for at least <paramref name="minCapacity"/> items if it has to be copied, and that the
            /// items start at index 0 of it.
            /// </summary>
            internal virtual void PrepareForWrite(int minCapacity) {
            }

            /// <summary>
            /// Gets index'th character of the string.
            /// Throws if the string includes invalid characters.
            /// </summary>
            public abstract char GetChar(int index);

            /// <summary>
            /// Gets index'th byte of the string.
            /// Throws if the string includes invalid characters.
            /// </summary>
            public abstract byte GetByte(int index);

            public abstract CharacterEnumerator/*!*/ GetCharacters();
            public abstract IEnumerable<byte>/*!*/ GetBytes();

            public abstract int OrdinalCompareTo(string/*!*/ str);

            public abstract int OrdinalCompareTo(Content/*!*/ content);
            public abstract int ReverseOrdinalCompareTo(BinaryContent/*!*/ content);
            public abstract int ReverseOrdinalCompareTo(CharArrayContent/*!*/ content);
            public abstract int ReverseOrdinalCompareTo(StringContent/*!*/ content);
            
            /// <summary>
            /// Returns a slice of the content. The size of the slice could be less than the requested count if there is not enough data in the content.
            /// Returns an empty content if start is greater than the size of the content.
            /// The owner of the result is the current owner.
            /// </summary>
            public abstract Content/*!*/ GetSlice(int start, int count);
            public abstract string/*!*/ GetStringSlice(int start, int count);
            public abstract byte[]/*!*/ GetBinarySlice(int start, int count);

            public abstract bool StartsWith(char c);

            public abstract int IndexOf(char c, int start, int count);
            public abstract int IndexOf(byte b, int start, int count);
            public abstract int IndexOf(string/*!*/ str, int start, int count);
            public abstract int IndexOf(byte[]/*!*/ bytes, int start, int count);
            public abstract int IndexIn(Content/*!*/ str, int start, int count);

            public abstract int LastIndexOf(char c, int start, int count);
            public abstract int LastIndexOf(byte b, int start, int count);
            public abstract int LastIndexOf(string/*!*/ str, int start, int count);
            public abstract int LastIndexOf(byte[]/*!*/ bytes, int start, int count);
            public abstract int LastIndexIn(Content/*!*/ str, int start, int count);

            public abstract Content/*!*/ Concat(Content/*!*/ content);
            public abstract Content/*!*/ ConcatTo(BinaryContent/*!*/ content);
            public abstract Content/*!*/ ConcatTo(CharArrayContent/*!*/ content);
            public abstract Content/*!*/ ConcatTo(StringContent/*!*/ content);

            // write:
            public abstract void Append(char c, int repeatCount);
            public abstract void Append(byte b, int repeatCount);
            public abstract void Append(string/*!*/ str, int start, int count);
            public abstract void Append(char[]/*!*/ chars, int start, int count);
            public abstract void Append(byte[]/*!*/ bytes, int start, int count);
            public abstract void Append(Stream/*!*/ stream, int count);

            public abstract void Append(Content/*!*/ content, int start, int count);
            public abstract void AppendTo(BinaryContent/*!*/ content, int start, int count);
            public abstract void AppendTo(CharArrayContent/*!*/ content, int start, int count);
            public abstract void AppendTo(StringContent/*!*/ content, int start, int count);

            public abstract void AppendFormat(IFormatProvider provider, string/*!*/ format, object[]/*!*/ args);
            
            public abstract void Insert(int index, char c);
            public abstract void Insert(int index, byte b);
            public abstract void Insert(int index, string/*!*/ str, int start, int count);
            public abstract void Insert(int index, char[]/*!*/ chars, int start, int count);
            public abstract void Insert(int index, byte[]/*!*/ bytes, int start, int count);
            public abstract void InsertTo(Content/*!*/ str, int index, int start, int count);

            public abstract void SetByte(int index, byte b);
            public abstract void SetChar(int index, char c);

            public abstract void Remove(int start, int count);

            public abstract void Write(int offset, byte[]/*!*/ value, int start, int count);
            public abstract void Write(int offset, byte value, int repeatCount);
        }
    }
}

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
using System.Diagnostics;
using System.IO;
using System.Text;
using IronRuby.Compiler;
using IronRuby.Runtime;
using Microsoft.Scripting.Utils;
using System.Globalization;

namespace IronRuby.Builtins {

    // Doesn't implement IRubyObject since that would require to hold on a RubyClass object and flow it into each factory.
    // We don't want to do so since it would make libraries complex and frozen per-appdomain singletons impossible.
    // It would also consume more memory while the string subclassing is not a common scenario.
    // To allow inheriting from String in Ruby, we need a subclass that implements IRubyObject.
    // We could genrate one the first time a String is subclassed. Having it defined explicitly (MutableString.Subclass) however
    // saves that code gen and also makes it simpler to detect whether or not we need to create a subclass of a string fast. 
    // That's a common operation String methods do.
    [Serializable]
    [DebuggerDisplay("{GetDebugValue()}", Type = "{GetDebugType()}")]
    public partial class MutableString : IEquatable<MutableString>, IComparable<MutableString>, IComparable, IRubyObjectState, IDuplicable {
        private Content/*!*/ _content;
        private RubyEncoding/*!*/ _encoding;
        
        private uint _flags = AsciiUnknownFlag | SurrogatesUnknownFlag | CharIndexChangedFlags;

        // true if frozen:
        private const uint IsFrozenFlag = 1;

        // TODO: obsolete? (supported 1.8 behavior?)
        // set every time a change occurs (visible externally):
        private const uint HasChangedFlag = 1 << 1;

        // set every time a change occurs, used to track CharArrayContent._immutableSnapshot validity:
        private const uint HasChangedCharArrayToStringFlag = 1 << 2;

        // Set every time a change occurs: retires the character index table of mutable content
        // (MutableString.CharIndex.cs).
        private const uint CharIndexChangedFlags = 1 << 14;

        private const uint HasChangedFlags = HasChangedFlag | HasChangedCharArrayToStringFlag | CharIndexChangedFlags;

        // true if all bytes/characters are < x80 and the encoding is ASCII-identity
        private const uint IsAsciiFlag = 1 << 3;

        // true if IsAscii flag is not up-to-date:
        private const uint AsciiUnknownFlag = 1 << 4;

        // true if there are no surrogate characters:
        private const uint NoSurrogatesFlag = 1 << 5;

        // true if NoHighCharacters flag is not up-to-date:
        private const uint SurrogatesUnknownFlag = 1 << 6;

        // true if tainted:
        private const uint IsTaintedFlag = 1 << 8;

        // true if untrusted:
        private const uint IsUntrustedFlag = 1 << 9;

        // true if the content should be copied on mutation:
        private const uint CopyOnWriteFlag = 1 << 10;

        // True for a string literal in a file that said nothing about frozen_string_literal.
        // Such a string is mutable, but Ruby 3.4 onwards warns the first time it is mutated that
        // it will be frozen in some future version. The bit rides the same test as frozen and
        // copy-on-write, so an ordinary string pays nothing for it.
        private const uint IsChilledFlag = 1 << 11;

        // rb_str_locktmp: the string is lent out - IO::Buffer.for hands a window onto its
        // bytes to Ruby code - and must not be modified until it is handed back. Unlike
        // freezing this is temporary and reversible, so it gets its own bit.
        private const uint IsTemporarilyLockedFlag = 1 << 12;

        // A chilled string that came from Symbol#to_s rather than from a literal. MRI words the
        // warning differently for those, naming the symbol - which is this very string's content,
        // so nothing beyond the bit has to be remembered.
        private const uint IsChilledSymbolStringFlag = 1 << 13;

        private const uint MutationGuardFlags =
            IsFrozenFlag | CopyOnWriteFlag | IsChilledFlag | IsTemporarilyLockedFlag;

        /// <summary>
        /// Called the first time a chilled string is mutated. Set by RubyContext, because the
        /// report has to reach the Ruby $stderr and a MutableString has no way to find one.
        /// With more than one runtime in the process the last one to start wins; that only ever
        /// costs a deprecation warning going to the wrong stream.
        /// </summary>
        internal static Action<MutableString> ChilledMutationReporter;
        
        // The instance is frozen so that it can be shared, but it should not be used in places where
        // it will be accessible from user code as the user code could try to mutate it.
        public static readonly MutableString FrozenEmpty = CreateEmpty().Freeze();

        #region Constructors

        /// <summary>
        /// Sets content to a different but equivalent representation.
        /// </summary>
        private void SetContent(Content/*!*/ content) {
            Assert.NotNull(content);
            content.SetOwner(this);
            _content = content;
            _flags |= CharIndexChangedFlags;
        }

        private void SetEncoding(RubyEncoding/*!*/ encoding) {
            uint flags = _flags;

            // we can extract some useful information from the target encoding:
            if (!encoding.IsAsciiIdentity) {
                flags &= ~(AsciiUnknownFlag | IsAsciiFlag);
            } else if (_encoding != null && !_encoding.IsAsciiIdentity) {
                // No character in a non-ascii-identity encoding counts as ASCII, so the flag
                // says "not ASCII" whatever the bytes are. Coming back to an encoding where the
                // bytes do decide, it has to be worked out again - otherwise a UTF-16LE string
                // forced to BINARY still claims not to be ASCII, and comparing it with a plain
                // String takes the wrong path.
                flags |= AsciiUnknownFlag;
            }

            if (encoding.InUnicodeBasicPlane) {
                flags = (flags & ~SurrogatesUnknownFlag) | NoSurrogatesFlag;
            } else {
                flags |= SurrogatesUnknownFlag;
            }

            _flags = flags | HasChangedFlag | CharIndexChangedFlags;
            _encoding = encoding;
        }

        internal MutableString(Content/*!*/ content, RubyEncoding/*!*/ encoding) {
            Assert.NotNull(content, encoding);
            SetEncoding(encoding);
            SetContent(content);
            // every other constructor comes through here; off unless the objspace library asked for it
            if (ObjectTracking.Enabled) {
                ObjectTracking.Track(this);
            }
        }

        // creates a copy including the taint flag:
        protected MutableString(MutableString/*!*/ str) 
            : this(str._content.Clone(), str._encoding) {
            IsTainted = str.IsTainted;
            IsUntrusted = str.IsUntrusted;
            // The same characters in the same encoding: what is known about them still holds, so
            // a copy - the frozen one each MatchData keeps, say - need not scan them again.
            const uint CharacterFlags = IsAsciiFlag | AsciiUnknownFlag | NoSurrogatesFlag | SurrogatesUnknownFlag;
            _flags = (_flags & ~CharacterFlags) | (str._flags & CharacterFlags);
        }

        // mutable (doesn't make a copy of the array):
        private MutableString(char[]/*!*/ chars, RubyEncoding/*!*/ encoding)
            : this(new CharArrayContent(chars, null), encoding) {
        }

        // mutable (doesn't make a copy of the array):
        private MutableString(char[]/*!*/ chars, int count, RubyEncoding/*!*/ encoding)
            : this(new CharArrayContent(chars, count, null), encoding) {
        }

        // binary (doesn't make a copy of the array):
        private MutableString(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding)
            : this(new BinaryContent(bytes, null), encoding) {
        }

        // binary (doesn't make a copy of the array):
        // used by RubyBufferedStream:
        internal MutableString(byte[]/*!*/ bytes, int count, RubyEncoding/*!*/ encoding)
            : this(new BinaryContent(bytes, count, null), encoding) {
        }

        // immutable:
        private MutableString(string/*!*/ str, RubyEncoding/*!*/ encoding)
            : this(new StringContent(str, null), encoding) {
        }

        // mutable (visible for subclasses):
        protected MutableString(RubyEncoding/*!*/ encoding)
            : this(new CharArrayContent(Utils.EmptyChars, 0, null), encoding) {
        }

        // Ruby allocator
        public MutableString() 
            : this(String.Empty, RubyEncoding.Binary) {
        }

        #endregion

        #region Factories

        public static MutableString/*!*/ CreateMutable(RubyEncoding/*!*/ encoding) {
            return new MutableString(encoding);
        }

        public static MutableString/*!*/ CreateMutable(int capacity, RubyEncoding/*!*/ encoding) {
            ContractUtils.Requires(capacity >= 0, "Capacity must be greater or equal to zero.");
            ContractUtils.RequiresNotNull(encoding, "encoding");
            return new MutableString(new char[capacity], 0, encoding);
        }

        public static MutableString/*!*/ CreateMutable(string/*!*/ str, RubyEncoding/*!*/ encoding) {
            ContractUtils.RequiresNotNull(str, "str");
            ContractUtils.RequiresNotNull(encoding, "encoding");
            return new MutableString(str, encoding);
        }

        /// <summary>
        /// Creates an instace initialized with given ASCII string.
        /// </summary>
        /// <remarks>
        /// The ASCII-ness of <paramref name="str"/> is not verified (unless compiled in debug build).
        /// If the string contains any non-ASCII characters subsequent operations might produce incorrect results.
        /// </remarks>
        public static MutableString CreateAscii(string/*!*/ str) {
            ContractUtils.RequiresNotNull(str, "str");
            Debug.Assert(str.IsAscii());
            var result = Create(str, RubyEncoding.Ascii);
            result._flags = IsAsciiFlag | NoSurrogatesFlag;
            return result;
        }

        public static MutableString/*!*/ Create(string/*!*/ str) {
            ContractUtils.RequiresNotNull(str, "str");
            return str.IsAscii() ? CreateAscii(str) : Create(str, RubyEncoding.UTF8);
        }
        
        public static MutableString/*!*/ Create(string/*!*/ str, RubyEncoding/*!*/ encoding) {
            ContractUtils.RequiresNotNull(str, "str");
            ContractUtils.RequiresNotNull(encoding, "encoding");
            return new MutableString(str, encoding);
        }

        public static MutableString/*!*/ CreateBinary() {
            return new MutableString(Utils.EmptyBytes, 0, RubyEncoding.Binary);
        }

        public static MutableString/*!*/ CreateBinary(RubyEncoding/*!*/ encoding) {
            return new MutableString(Utils.EmptyBytes, 0, encoding);
        }

        public static MutableString/*!*/ CreateBinary(int capacity) {
            return CreateBinary(capacity, RubyEncoding.Binary);
        }

        public static MutableString/*!*/ CreateBinary(int capacity, RubyEncoding/*!*/ encoding) {
            ContractUtils.Requires(capacity >= 0, "Capacity must be greater or equal to zero.");
            ContractUtils.RequiresNotNull(encoding, "encoding");
            return new MutableString(new byte[capacity], 0, encoding);
        }

        public static MutableString/*!*/ CreateBinary(byte[]/*!*/ bytes) {
            return CreateBinary(bytes, RubyEncoding.Binary);
        }

        public static MutableString/*!*/ CreateBinary(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            ContractUtils.RequiresNotNull(bytes, "bytes");
            ContractUtils.RequiresNotNull(encoding, "encoding");
            return new MutableString(ArrayUtils.Copy(bytes), encoding);
        }

        public static MutableString/*!*/ CreateBinary(List<byte>/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            ContractUtils.RequiresNotNull(bytes, "bytes");
            ContractUtils.RequiresNotNull(encoding, "encoding");
            return new MutableString(bytes.ToArray(), encoding);
        }

        /// <summary>
        /// Creates an instance of MutableString with content and taint copied from a given string.
        /// </summary>
        public static MutableString/*!*/ Create(MutableString/*!*/ str) {
            ContractUtils.RequiresNotNull(str, "str");
            return new MutableString(str);
        }

        // used by RubyOps:
        internal static MutableString/*!*/ CreateInternal(MutableString str, RubyEncoding/*!*/ encoding) {
            if (str != null) {
                // "...#{str}..."
                return new MutableString(str);
            } else {
                // empty literal: "...#{nil}..."
                return CreateMutable(String.Empty, encoding);
            }
        }
        
        /// <summary>
        /// Creates a blank instance of self type with no flags set.
        /// Copies encoding from the current class.
        /// </summary>
        public virtual MutableString/*!*/ CreateInstance() {
            return new MutableString(_encoding);
        }

        // creates an instance of self type with given content and encoding:
        internal virtual MutableString/*!*/ CreateInstance(Content/*!*/ content, RubyEncoding/*!*/ encoding) {
            return new MutableString(content, encoding);
        }

        /// <summary>
        /// A blank String for a method that *derives* a new string from this one. Ruby 3.0 made
        /// every such method answer with String even when the receiver is a subclass of it -
        /// S.new("ab").upcase is a String, not an S - and only #dup, #clone and #+@ carry the
        /// class over. Those go through CreateInstance and Clone, which still do.
        /// </summary>
        public MutableString/*!*/ CreateDerived() {
            return new MutableString(_encoding);
        }

        internal MutableString/*!*/ CreateDerived(Content/*!*/ content, RubyEncoding/*!*/ encoding) {
            return new MutableString(content, encoding);
        }

        /// <summary>A copy of this instance as a plain String. See CreateDerived.</summary>
        public MutableString/*!*/ CloneDerived() {
            return new MutableString(this);
        }

        public static MutableString/*!*/ CreateEmpty() {
            return MutableString.Create(String.Empty, RubyEncoding.Binary);
        }

        public static MutableString/*!*/ CreateEmpty(RubyEncoding/*!*/ encoding) {
            return MutableString.Create(String.Empty, encoding);
        }

        /// <summary>
        /// Creates a copy of this instance, including content and taint.
        /// Doesn't copy frozen state and instance variables. 
        /// Preserves the class of the String.
        /// </summary>
        public virtual MutableString/*!*/ Clone() {
            return new MutableString(this);
        }

        /// <summary>
        /// Creates an empty copy of this instance, taint and instance variables. 
        /// </summary>
        public MutableString/*!*/ Duplicate(RubyContext/*!*/ context, bool copySingletonMembers, MutableString/*!*/ result) {
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            return Duplicate(context, copySingletonMembers, CreateInstance());
        }

        public static MutableString[]/*!*/ MakeArray(ICollection<string>/*!*/ stringCollection, RubyEncoding/*!*/ encoding) {
            ContractUtils.RequiresNotNull(stringCollection, "stringCollection");
            ContractUtils.RequiresNotNull(encoding, "encoding");

            MutableString[] result = new MutableString[stringCollection.Count];
            int i = 0;
            foreach (var str in stringCollection) {
                result[i++] = MutableString.Create(str, encoding);
            }
            return result;
        }

        #endregion

        #region Versioning, Encoding, HashCode, and Flags

        /// <summary>
        /// Returns true if the characters included in the string map 1:1 to their encoded repr (bytes).
        /// (the encoding is binary or the string includes ASCII only).
        /// Returns false if the string encoding is not ASCII-identity.
        /// Doesn't inspect the content of the string if the ASCII flag is not valid.
        /// </summary>
        public bool HasByteCharacters {
            get {
                return (_flags & (AsciiUnknownFlag | IsAsciiFlag)) == IsAsciiFlag 
                    || _encoding == RubyEncoding.Binary;
            }
        }

        public bool DetectByteCharacters() {
            return _encoding == RubyEncoding.Binary || IsAscii();
        }
        
        /// <summary>
        /// All characters in the string are encoded as single bytes.
        /// Returns false if the string encoding is not ASCII-identity.
        /// Doesn't inspect the content of the string if the ASCII flag is not valid.
        /// </summary>
        public bool HasSingleByteCharacters {
            get {
                return (_flags & (AsciiUnknownFlag | IsAsciiFlag)) == IsAsciiFlag
                    || _encoding.IsSingleByteCharacterSet;
            }
        }

        public bool DetectSingleByteCharacters() {
            return _encoding.IsSingleByteCharacterSet || IsAscii();
        }

        /// <summary>
        /// The slow path of a mutation: the string is frozen, shared, chilled, or some
        /// combination. Answers the flags the caller should go on to store - it must not reuse
        /// the ones it read, or it would put the copy-on-write and chilled bits straight back.
        /// </summary>
        private uint FrozenOrCopyOnWrite(uint flags) {
            if ((flags & IsFrozenFlag) != 0) {
                throw RubyExceptions.CreateStringFrozenError(this);
            }

            if ((flags & IsTemporarilyLockedFlag) != 0) {
                throw RubyExceptions.CreateTemporarilyLockedError();
            }

            if ((flags & CopyOnWriteFlag) != 0) {
                // TODO: we can do better if the representation is being changed: we don't need to copy the data twice
                _content = _content.Clone();
                flags &= ~CopyOnWriteFlag;
            }

            if ((flags & IsChilledFlag) != 0) {
                // Report once: after this the string is an ordinary mutable one.
                // The symbol bit stays set: the reporter reads it to choose the wording, and it
                // means nothing once the chilled bit is gone.
                flags &= ~IsChilledFlag;
                _flags = flags;

                var reporter = ChilledMutationReporter;
                if (reporter != null) {
                    reporter(this);
                }
            }

            _flags = flags;
            return flags;
        }

        /// <summary>
        /// Where a string literal was written, for --debug-frozen-string-literal. Off that flag
        /// nothing is ever recorded, so ordinary runs pay neither the lookup nor the memory; the
        /// entries go away with the strings they describe.
        /// </summary>
        private static System.Runtime.CompilerServices.ConditionalWeakTable<MutableString, string> _literalSites;

        public static MutableString/*!*/ RecordLiteralSite(MutableString/*!*/ str, string/*!*/ site) {
            var table = _literalSites;
            if (table == null) {
                System.Threading.Interlocked.CompareExchange(ref _literalSites,
                    new System.Runtime.CompilerServices.ConditionalWeakTable<MutableString, string>(), null);
                table = _literalSites;
            }

            // A frozen literal is shared, so the same instance can come back for a second literal;
            // MRI names the first one it saw too.
            string existing;
            if (!table.TryGetValue(str, out existing)) {
                table.Add(str, site);
            }
            return str;
        }

        public static string GetLiteralSite(MutableString/*!*/ str) {
            var table = _literalSites;
            string site;
            return table != null && table.TryGetValue(str, out site) ? site : null;
        }

        /// <summary>
        /// Symbol#to_s hands back a chilled string too, and MRI names the symbol in the warning
        /// rather than calling it a literal. The symbol is the string's own content, so marking
        /// it takes nothing but a bit - and Symbol#to_s is hot enough that it had better not.
        /// </summary>
        public static MutableString/*!*/ ChillAsSymbolString(MutableString/*!*/ str) {
            str._flags |= IsChilledFlag | IsChilledSymbolStringFlag;
            return str;
        }

        public bool IsChilledSymbolString {
            get { return (_flags & IsChilledSymbolStringFlag) != 0; }
        }

        /// <summary>
        /// Gives a copy the chilled state of the string it came from, which is what #clone does
        /// with it. Whatever the original would have warned about, the copy warns about too.
        /// </summary>
        public static void CopyChilledState(MutableString/*!*/ source, MutableString/*!*/ copy) {
            if (!source.IsChilled) {
                return;
            }

            if (source.IsChilledSymbolString) {
                ChillAsSymbolString(copy);
            } else {
                copy.Chill();
            }
        }

        /// <summary>
        /// A literal in a file with no frozen_string_literal comment: mutable, but the first
        /// mutation is worth a deprecation warning.
        /// </summary>
        public MutableString/*!*/ Chill() {
            _flags |= IsChilledFlag;
            return this;
        }

        public bool IsChilled {
            get { return (_flags & IsChilledFlag) != 0; }
        }

        /// <summary>
        /// Reports, once, that a chilled string is being changed in a way that is not a write to
        /// its characters: given a singleton class, or given an instance variable. MRI warns about
        /// those too, because a frozen string could not have either.
        /// </summary>
        public static void ReportChilledChange(object obj) {
            var str = obj as MutableString;
            if (str == null || (str._flags & IsChilledFlag) == 0) {
                return;
            }

            str._flags &= ~IsChilledFlag;
            var reporter = ChilledMutationReporter;
            if (reporter != null) {
                reporter(str);
            }
        }

        private void MutateContent(uint setFlags) {
            uint flags = _flags;
            if ((flags & MutationGuardFlags) != 0) {
                flags = FrozenOrCopyOnWrite(flags);
            }
            _flags = flags | setFlags;
        }

        /// <summary>
        /// Non-specific mutation. Can affect ascii-ness and surrogate-ness of the string.
        /// </summary>
        private void Mutate() {
            MutateContent(HasChangedFlags | AsciiUnknownFlag | SurrogatesUnknownFlag);
        }

        /// <summary>
        /// Set, append or insert a single char.
        /// </summary>
        private void MutateOne(char c) {
            uint flags = _flags;
            if ((flags & MutationGuardFlags) != 0) {
                flags = FrozenOrCopyOnWrite(flags);
            }
            if (c >= 0x80) {
                if (Tokenizer.IsSurrogate(c)) {
                    flags &= ~(AsciiUnknownFlag | IsAsciiFlag | SurrogatesUnknownFlag | NoSurrogatesFlag);
                } else {
                    flags &= ~(AsciiUnknownFlag | IsAsciiFlag);
                }
            }
            _flags = flags | HasChangedFlags;
        }

        /// <summary>
        /// Set, append or insert a single byte.
        /// </summary>
        private void MutateOne(byte b) {
            uint flags = _flags;
            if ((flags & MutationGuardFlags) != 0) {
                flags = FrozenOrCopyOnWrite(flags);
            }
            if (b >= 0x80) {
                flags &= ~(AsciiUnknownFlag | IsAsciiFlag);
            } else {
                flags |= AsciiUnknownFlag;
            }
            _flags = flags | SurrogatesUnknownFlag | HasChangedFlags;
        }

        /// <summary>
        /// Operation preserves ascii-ness and surrogate-ness of the string.
        /// </summary>
        private void MutatePreserveAsciiness() {
            MutateContent(HasChangedFlags);
        }

        /// <summary>
        /// Operation removes characters or bytes.
        /// </summary>
        private void MutateRemove() {
            // If the string was ascii before the operation it is ascii afterwards.
            // If the string had no surrogates it still doesn't have them.
            MutateContent(
                ((_flags & IsAsciiFlag) != 0 ? 0 : AsciiUnknownFlag) | 
                ((_flags & NoSurrogatesFlag) != 0 ? 0 : SurrogatesUnknownFlag) |
                HasChangedFlags 
            );
        }

        /// <summary>
        /// Prepares the string for mutation that combines its content with content of another mutable string.
        /// </summary>
        private void Mutate(MutableString/*!*/ other) {
            RubyEncoding newEncoding = RequireCompatibleEncoding(other);
            Mutate();
            SetEncoding(newEncoding);
        }

        /// <summary>
        /// Operation only adds bytes or characters of unknown ascii-ness, keeping the encoding.
        /// A string known to contain a non-ASCII character still contains it afterwards, so that
        /// knowledge is kept rather than paying for a rescan of the whole content.
        /// </summary>
        private void MutateAppend() {
            MutateContent(
                ((_flags & (AsciiUnknownFlag | IsAsciiFlag)) == 0 ? 0 : AsciiUnknownFlag) |
                SurrogatesUnknownFlag |
                HasChangedFlags
            );
            if ((_flags & AsciiUnknownFlag) != 0) {
                _flags &= ~IsAsciiFlag;
            }
        }

        /// <summary>
        /// Prepares the string for appending or inserting (a part of) another string, which only ever adds content.
        /// Unlike <see cref="Mutate(MutableString)"/> this keeps what is known about the ascii-ness of the result,
        /// so that building a string piece by piece doesn't rescan it after every append (which made appends quadratic).
        /// </summary>
        /// <param name="whole">True if all of <paramref name="other"/> is added, false for a part of it.</param>
        private void MutateAppend(MutableString/*!*/ other, bool whole) {
            RubyEncoding newEncoding = RequireCompatibleEncoding(other);

            // an empty string is ASCII and has no surrogates, whether or not that has been worked out:
            bool isEmpty = _content.IsEmpty;

            uint ascii = AsciiUnknownFlag;
            if (_encoding.IsAsciiIdentity && newEncoding.IsAsciiIdentity) {
                uint known = isEmpty ? IsAsciiFlag : _flags & (AsciiUnknownFlag | IsAsciiFlag);
                if (known == 0) {
                    // a non-ASCII byte/character stays in the string:
                    ascii = 0;
                } else if (other._encoding.IsAsciiIdentity) {
                    if (whole) {
                        // O(appended length):
                        bool otherAscii = other.IsAscii();
                        if (!otherAscii) {
                            ascii = 0;
                        } else if (known == IsAsciiFlag) {
                            ascii = IsAsciiFlag;
                        }
                    } else if (known == IsAsciiFlag && other.KnowsAscii && other.IsAscii()) {
                        // a part of an ASCII string is ASCII (don't scan all of other for a part of it):
                        ascii = IsAsciiFlag;
                    }
                }
            }

            // Surrogates only mean something for character representations. Two such strings free of
            // surrogates hold no invalid bytes either (those are escaped as lone surrogates), so
            // joining them can't create one:
            uint surrogates = SurrogatesUnknownFlag;
            if (newEncoding == _encoding && !IsBinary && !other.IsBinary &&
                (isEmpty || (_flags & (SurrogatesUnknownFlag | NoSurrogatesFlag)) == NoSurrogatesFlag) &&
                (other.KnowsSurrogates || whole) && !other.HasSurrogates()) {
                surrogates = NoSurrogatesFlag;
            }

            MutateContent(HasChangedFlags);
            _flags = (_flags & ~(AsciiUnknownFlag | IsAsciiFlag | SurrogatesUnknownFlag | NoSurrogatesFlag)) | ascii | surrogates;
            if (newEncoding != _encoding) {
                SetEncoding(newEncoding);
            }
        }

        /// <summary>
        /// Checks if the other string's encoding is compatible with this string's encoding.
        /// If it is returns the encoding that should be used for the result of the operation.
        /// Returns a <c>null</c> reference otherwise.
        /// </summary>
        public RubyEncoding GetCompatibleEncoding(MutableString/*!*/ other) {
            return GetCompatibleEncoding(this, _encoding, other, other.Encoding);
        }

        public RubyEncoding GetCompatibleEncoding(RubyEncoding/*!*/ encoding) {
            return GetCompatibleEncoding(this, _encoding, null, encoding);
        }

        public static RubyEncoding GetCompatibleEncoding(RubyEncoding/*!*/ encoding1, RubyEncoding/*!*/ encoding2) {
            return GetCompatibleEncoding(null, encoding1, null, encoding2);
        }

        /// <summary>
        /// MRI's rb_enc_compatible / enc_compatible_latter. A null string means "an object that
        /// merely carries an encoding" (an Encoding object, a Regexp, ...) rather than a String;
        /// MRI treats those differently, which is why the two are passed separately.
        /// </summary>
        public static RubyEncoding GetCompatibleEncoding(MutableString str1, RubyEncoding/*!*/ encoding1,
            MutableString str2, RubyEncoding/*!*/ encoding2) {

            if (encoding1 == encoding2) {
                return encoding1;
            }

            if (str2 != null && str2.IsEmpty) {
                return encoding1;
            }

            if (str1 != null && str2 != null && str1.IsEmpty) {
                return (encoding1.IsAsciiIdentity && str2.IsAscii()) ? encoding1 : encoding2;
            }

            if (!encoding1.IsAsciiIdentity || !encoding2.IsAsciiIdentity) {
                return null;
            }

            // objects whose encoding is the encoding of their contents
            if (str2 == null && encoding2 == RubyEncoding.Ascii) {
                return encoding1;
            }
            if (str1 == null && encoding1 == RubyEncoding.Ascii) {
                return encoding2;
            }

            // MRI swaps the two operands so that the String is first, but deliberately does not
            // swap enc1/enc2 along with them.
            if (str1 == null) {
                str1 = str2;
                str2 = null;
            }

            if (str1 != null) {
                bool ascii1 = str1.IsAscii();
                if (str2 != null) {
                    bool ascii2 = str2.IsAscii();
                    if (ascii1 != ascii2) {
                        return ascii1 ? encoding2 : encoding1;
                    }
                    if (ascii2) {
                        return encoding1;
                    }
                }
                if (ascii1) {
                    return encoding2;
                }
            }

            return null;
        }

        public RubyEncoding/*!*/ RequireCompatibleEncoding(MutableString/*!*/ other) {
            var result = GetCompatibleEncoding(other);
            if (result == null) {
                throw RubyExceptions.CreateEncodingCompatibilityError(_encoding, other.Encoding);
            }
            return result;
        }

        /// <summary>
        /// Changes encoding to the specified one. 
        /// The resulting string might contain byte-sequences that don't represent valid characters in the target encoding.
        /// </summary>
        public void ForceEncoding(RubyEncoding/*!*/ newEncoding) {
            ContractUtils.RequiresNotNull(newEncoding, "newEncoding");

            if (_encoding == newEncoding) {
                return;
            }

            if (IsBinary) {
                SetEncoding(newEncoding);
                return;
            }

            // If the representation is character based and includes non-ascii chcaracters then we need 
            // to switch to binary repr before we change the encoding so that the binary repr of the string is preserved.

            // this caches hash-code, which we need to invalidate due to encoding change:
            bool isAscii = IsAscii();
            Mutate();

            // An all-ASCII character representation only keeps its bytes if both the old and the
            // new encoding encode ASCII as itself. "ab" in UTF-16LE is 4 bytes, so forcing a
            // character-based "ab" to UTF-16LE without switching to bytes first would invent two
            // NUL bytes (and forcing a UTF-16LE string to UTF-8 would drop them).
            if (isAscii && _encoding.IsAsciiIdentity && newEncoding.IsAsciiIdentity) {
                SetEncoding(newEncoding);
            } else {
                SwitchToBytes();
                SetEncoding(newEncoding);
            }
        }

        /// <summary>
        /// Assumes the content to be encoded in fromEncoding and trancodes it into toEncoding.
        /// </summary>
        /// <exception cref="EncoderFallbackException">Invalid data.</exception>
        /// <exception cref="DecoderFallbackException">Invalid data.</exception>
        /// <exception cref="RuntimeError">The string is frozen.</exception>
        public void Transcode(RubyEncoding/*!*/ fromEncoding, RubyEncoding/*!*/ toEncoding) {
            if (fromEncoding == toEncoding && _encoding == fromEncoding) {
                return;
            }

            bool isAscii = IsAscii();
            Mutate();

            if (isAscii) {
                SetEncoding(toEncoding);
                return;
            }
            
            // fromEncoding -> UTF16:
            bool switchToChars;
            if (IsBinary) {
                if (fromEncoding != _encoding) {
                    SetEncoding(fromEncoding);
                }
                switchToChars = true;
            } else if (fromEncoding != _encoding) {
                try {
                    _content = _content.SwitchToBinaryContent();
                } catch (EncoderFallbackException e) {
                    throw RubyExceptions.CreateInvalidByteSequenceError(e, _encoding);
                }
                SetEncoding(fromEncoding);
                switchToChars = true;
            } else {
                switchToChars = false;
            }

            if (switchToChars) {
                try {
                    _content = _content.SwitchToStringContent();
                } catch (DecoderFallbackException e) {
                    throw RubyExceptions.CreateInvalidByteSequenceError(e, fromEncoding);
                }
            }

            // UTF16 -> toEncoding:
            SetEncoding(toEncoding);
            try {
                _content.CheckEncoding();
            } catch (EncoderFallbackException e) {
                throw RubyExceptions.CreateTranscodingError(e, fromEncoding, toEncoding);
            }
        }

        /// <summary>
        /// Returns hash code of the string. The hash code is the same regardless of the internal string representation 
        /// and also equal to <see cref="System.String.GetHashCode"/> if the string only contains ASCII characters, is binary-encoded,
        /// or UTF8 encoded. The hash code is not cached.
        /// </summary>
        public override int GetHashCode() {
            return _content.CalculateHashCode();
        }

        /// <summary>
        /// Returns true if the string only contains characters U+007F or lower.
        /// Scans the string unless the information is cached (<see cref="KnowsAscii"/>).
        /// </summary>
        public bool IsAscii() {
            var flags = _flags;

            if ((flags & AsciiUnknownFlag) != 0) {
                if (_encoding.IsAsciiIdentity) {
                    flags = _content.UpdateCharacterFlags(_flags);
                } else {
                    // no characters in non-ascii-identity encoding are considered to have "ascii" property:
                    flags &= ~(AsciiUnknownFlag | IsAsciiFlag);
                }

                _flags = flags;
            }

            return (flags & IsAsciiFlag) != 0;
        }

        /// <summary>
        /// Returns true if a subsequent call to <see cref="IsAscii"/> will be O(1) operation, otherwise it is O(N) operation,
        /// where N is the number of bytes or characters of the string.
        /// </summary>
        public bool KnowsAscii {
            get { return (_flags & AsciiUnknownFlag) == 0 || !_encoding.IsAsciiIdentity; }
        }

        /// <summary>
        /// Returns true if the string contains any surrogate characters.
        /// Scans the string unless the information is cached (<see cref="KnowsSurrogates"/>).
        /// The property is pre-set for encodings whose decoders don't produce surrogates.
        /// The result is undefined if the string representation is binary.
        /// </summary>
        public bool HasSurrogates() {
            Debug.Assert(!IsBinary);

            var flags = _flags;

            if ((flags & SurrogatesUnknownFlag) != 0) {
                if (_encoding.InUnicodeBasicPlane) {
                    flags = flags & ~SurrogatesUnknownFlag | NoSurrogatesFlag;
                } else {
                    flags = _content.UpdateCharacterFlags(_flags);
                }
                _flags = flags;
            }

            return (flags & NoSurrogatesFlag) == 0;
        }

        /// <summary>
        /// Returns true if a subsequent call to <see cref="HasSurrogates"/> will be O(1) operation, otherwise it is O(N) operation,
        /// where N is the number of characters of the string (the property has no meaning for binary represented strings).
        /// </summary>
        public bool KnowsSurrogates {
            get {
                return (_flags & SurrogatesUnknownFlag) == 0 || _encoding.InUnicodeBasicPlane;
            }
        }

        public bool IsBinary {
            get { return _content.GetType() == typeof(BinaryContent); }
        }

        public RubyEncoding/*!*/ Encoding {
            get { return _encoding; }
        }

        /// <summary>
        /// Checks if the string content is correctly encoded.
        /// </summary>
        /// <exception cref="EncoderFallbackException"></exception>
        /// <exception cref="DecoderFallbackException"></exception>
        public MutableString/*!*/ CheckEncoding() {
            _content.CheckEncoding();
            return this;
        }

        public bool ContainsInvalidCharacters() {
            // Nothing is invalid in a dummy encoding, which says nothing about its bytes.
            if (_encoding.IsDummy || _encoding == RubyEncoding.Binary) {
                return false;
            }

            // The checks below use the cached character flags so that a string that doesn't change
            // isn't validated in full again and again (Regexp matching validates its input on every
            // match, which made String#split/#scan/#gsub quadratic).

            // ASCII is valid in any ASCII-compatible encoding:
            if (_encoding.IsAsciiIdentity && IsAscii()) {
                return false;
            }

            // An invalid byte in a character representation is escaped as a lone surrogate, and
            // any other character without a surrogate is representable in UTF-8:
            if (_encoding == RubyEncoding.UTF8 && !IsBinary && !HasSurrogates()) {
                return false;
            }

            return _content.ContainsInvalidCharacters();
        }

        public bool IsTainted {
            get {
                return (_flags & IsTaintedFlag) != 0; 
            }
            set {
                var flags = _flags;
                if ((flags & IsFrozenFlag) != 0) {
                    throw RubyExceptions.CreateStringFrozenError(this);
                }

                _flags = (flags & ~IsTaintedFlag) | (value ? IsTaintedFlag : 0);
            }
        }

        public bool IsUntrusted {
            get {
                return (_flags & IsUntrustedFlag) != 0;
            }
            set {
                var flags = _flags;
                if ((flags & IsFrozenFlag) != 0) {
                    throw RubyExceptions.CreateStringFrozenError(this);
                }

                _flags = (flags & ~IsUntrustedFlag) | (value ? IsUntrustedFlag : 0);
            }
        }

        public bool IsFrozen {
            get {
                return (_flags & IsFrozenFlag) != 0;
            }
        }

        public bool HasChanged {
            get { return (_flags & HasChangedFlag) != 0; }
        }

        internal void ClearFlag(uint flag) {
            _flags &= ~flag;
        }

        internal bool IsFlagSet(uint flag) {
            return (_flags & flag) != 0;
        }

        public void TrackChanges() {
            _flags &= ~HasChangedFlag;
        }

        void IRubyObjectState.Freeze() {
            Freeze();
        }

        public MutableString/*!*/ Freeze() {
            _flags |= IsFrozenFlag;
            return this;
        }

        public void RequireNotFrozen() {
            if (IsFrozen) {
                throw RubyExceptions.CreateStringFrozenError(this);
            }
        }

        /// <summary>
        /// rb_str_locktmp. A locked string refuses every mutation until it is unlocked,
        /// with a message of its own so the caller can tell it from a frozen string.
        /// </summary>
        public MutableString/*!*/ LockTemporarily() {
            if ((_flags & IsTemporarilyLockedFlag) != 0) {
                throw RubyExceptions.CreateTemporarilyLockedError();
            }
            RequireNotFrozen();
            _flags |= IsTemporarilyLockedFlag;
            return this;
        }

        /// <summary>rb_str_unlocktmp.</summary>
        public MutableString/*!*/ UnlockTemporarily() {
            _flags &= ~IsTemporarilyLockedFlag;
            return this;
        }

        public bool IsTemporarilyLocked {
            get { return (_flags & IsTemporarilyLockedFlag) != 0; }
        }

        /// <summary>
        /// Makes this string tainted if the specified string is tainted.
        /// </summary>
        public MutableString/*!*/ TaintBy(MutableString/*!*/ str) {
            IsTainted |= str.IsTainted;
            IsUntrusted |= str.IsUntrusted;
            return this;
        }

        /// <summary>
        /// Makes this string tainted if the specified object is tainted.
        /// </summary>
        public MutableString/*!*/ TaintBy(IRubyObjectState/*!*/ obj) {
            IsTainted |= obj.IsTainted;
            IsUntrusted |= obj.IsUntrusted;
            return this;
        }

        /// <summary>
        /// Makes this string tainted if the specified object is tainted.
        /// </summary>
        public MutableString/*!*/ TaintBy(object/*!*/ obj, RubyContext/*!*/ context) {
            bool tainted, untrusted;
            context.GetObjectTrust(obj, out tainted, out untrusted);
            IsTainted |= tainted;
            IsUntrusted |= untrusted;
            return this;
        }

        /// <summary>
        /// Makes this string tainted if the specified object is tainted.
        /// </summary>
        public MutableString/*!*/ TaintBy(object/*!*/ obj, RubyScope/*!*/ scope) {
            return TaintBy(obj, scope.RubyContext);
        }

        #endregion

        #region Regular Expressions (read-only)

        internal MutableString/*!*/ EscapeRegularExpression() {
            return CreateDerived(_content.EscapeRegularExpression(), _encoding);
        }

        #endregion

        #region Conversions (read-only)

        /// <summary>
        /// Returns a copy of the content in a form of an read-only string.
        /// The internal representation of the MutableString is preserved.
        /// </summary>
        /// <exception cref="DecoderFallbackException">Invalid characters present.</exception>
        public override string/*!*/ ToString() {
            return _content.ToString();
        }

        /// <summary>
        /// Like <see cref="ToString()"/>, but never throws: bytes that are invalid in the string's
        /// encoding are escaped rather than rejected. Use this wherever a CLR string is needed for
        /// display only - a CLR exception message, say - since a Ruby string is free to hold bytes
        /// that do not decode, and failing to render one is worse than rendering it with escapes.
        /// </summary>
        public string/*!*/ ToClrString() {
            try {
                return ToString();
            } catch (DecoderFallbackException) {
                return ToStringWithEscapedInvalidCharacters(_encoding);
            }
        }

        /// <summary>
        /// Switches the content to a byte array using the current encoding and decodes the binary into a string using the given encoding.
        /// </summary>
        /// <exception cref="DecoderFallbackException">Invalid characters present.</exception>
        public string/*!*/ ToString(Encoding/*!*/ encoding) {
            int count;
            byte[] bytes = _content.GetByteArray(out count);
            return encoding.GetString(bytes, 0, count);
        }

        /// <summary>
        /// Switches the content to a byte array using the current encoding and decodes the binary into a string using the given encoding.
        /// </summary>
        /// <exception cref="DecoderFallbackException">Invalid characters present.</exception>
        public string/*!*/ ToString(Encoding/*!*/ encoding, int start, int count) {
            byte[] bytes = GetByteArrayChecked(start, count);
            return encoding.GetString(bytes, start, count);
        }

        /// <summary>
        /// This property can be viewed using a string visualizer in a debugger, making it easy to inspect large or multi-line strings.
        /// </summary>
        internal string/*!*/ Dump {
            get { return ToString(); }
        }

        /// <summary>
        /// Returns a copy of the content in a form of an byte array.
        /// The internal representation of the MutableString is preserved.
        /// </summary>
        public byte[]/*!*/ ToByteArray() {
            return _content.ToByteArray();
        }

        /// <summary>
        /// Switches internal representation to textual.
        /// </summary>
        /// <returns>A copy of the internal representation unless it is read-only (string).</returns>
        public string/*!*/ ConvertToString() {
            return _content.ConvertToString();
        }

        /// <summary>
        /// Switches internal representation to binary.
        /// </summary>
        /// <returns>A copy of the internal representation.</returns>
        public byte[]/*!*/ ConvertToBytes() {
            return _content.ConvertToBytes();
        }

        /// <summary>
        /// Switches the underlying representation to bytes.
        /// </summary>
        /// <returns>Self.</returns>
        /// <exception cref="InvalidByteSequenceError">
        /// String content contains a character that isn't valid in the current encoding.
        /// </exception>
        public MutableString/*!*/ SwitchToBytes() {
            try {
                _content = _content.SwitchToBinaryContent();
            } catch (EncoderFallbackException e) {
                throw RubyExceptions.CreateInvalidByteSequenceError(e, _encoding);
            }
            return this;
        }

        /// <summary>
        /// Switches the underlying representation to characters.
        /// </summary>
        /// <returns>Self.</returns>
        /// <exception cref="InvalidByteSequenceError">
        /// String content is binary and contains byte sequence that doesn't represent a valid character in the current encoding.
        /// </exception>
        public MutableString/*!*/ SwitchToCharacters() {
            try {
                _content = _content.SwitchToStringContent();
            } catch (DecoderFallbackException e) {
                throw RubyExceptions.CreateInvalidByteSequenceError(e, _encoding);
            }
            return this;
        }

        /// <summary>
        /// Prepares the string for read-only character based operations.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// String content is binary and contains byte sequence that doesn't represent a valid character.
        /// </exception>
        public MutableString/*!*/ PrepareForCharacterRead() {
            // Switch if the content is not already char based or the bytes are not the same as the equivalent characters:
            if (IsBinary && !DetectByteCharacters()) {
                SwitchToCharacters();
            }

            return this;
        }

        /// <summary>
        /// Prepares the string for mutating character based operations that can potentially write arbitrary characters to the string.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// String content is binary and contains byte sequence that doesn't represent a valid character.
        /// </exception>
        public MutableString/*!*/ PrepareForCharacterWrite() {
            if (IsBinary) {
                SwitchToCharacters();
            } else {
                _content.SwitchToMutableContent();
            }
            return this;
        }

        // used by auto-conversions
        public static explicit operator string(MutableString/*!*/ self) {
            return self._content.ConvertToString();
        }

        // used by auto-conversions
        public static explicit operator byte[](MutableString/*!*/ self) {
            return self._content.ConvertToBytes();
        }

        // used by auto-conversions
        public static explicit operator char(MutableString/*!*/ self) {
            try {
                return self.GetChar(0);
            } catch (IndexOutOfRangeException) {
                throw RubyExceptions.CreateTypeConversionError("String", "System::Char");
            }
        }

        #endregion

        #region Comparisons (read-only)

        public override bool Equals(object other) {
            var ms = other as MutableString;
            if (ms != null) {
                return Equals(ms);
            }
            return Equals(other as string);
        }

        public bool Equals(MutableString other) {
            if (ReferenceEquals(other, null)) return false;

            // MRI's rb_str_comparable: a zero-length string is comparable with any string,
            // whatever the two encodings are. "".b == "" is true.
            if (!IsEmpty && !other.IsEmpty &&
                KnowsAscii && other.KnowsAscii && IsAscii() != other.IsAscii()) {
                return false;
            }

            return CompareTo(other) == 0;
        }

        public bool Equals(string other) {
            return CompareTo(other) == 0;
        }

        public int CompareTo(object other) {
            var ms = other as MutableString;
            if (ms != null) {
                return CompareTo(ms);
            }
            return CompareTo(other as string);
        }

        public int CompareTo(MutableString other) {
            if (ReferenceEquals(this, other)) return 0;
            if (ReferenceEquals(other, null)) return 1;

            // TODO: How does MRI deal with invalid characters, surrogates?
            if (_encoding != other._encoding) {
                bool bothAscii = true;
                if (!IsAscii()) {
                    SwitchToBytes();
                    bothAscii = false;
                }
                if (!other.IsAscii()) {
                    other.SwitchToBytes();
                    bothAscii = false;
                }
                int result = _content.OrdinalCompareTo(other._content);
                if (result != 0 || bothAscii) {
                    return result;
                }
                // MRI's rb_str_comparable: equal bytes in different encodings only differ if
                // both strings are non-empty; a zero-length string matches anything.
                if (IsEmpty || other.IsEmpty) {
                    return 0;
                }
                return _encoding.CompareTo(other._encoding);
            } else {
                return _content.OrdinalCompareTo(other._content);
            }
        }

        public int CompareTo(string other) {
            if (ReferenceEquals(other, null)) return 1;

            // TODO: How does MRI deal with invalid characters, surrogates?
            // TODO: for now, assume the other string is of the same encoding as this string (maybe we should compare binary UTF8 image?)
            return _content.OrdinalCompareTo(other);
        }

        #endregion

        #region Length (read-only)

        public static bool IsNullOrEmpty(MutableString/*!*/ str) {
            return ReferenceEquals(str, null) || str.IsEmpty;
        }

        public bool IsEmpty { 
            get { return _content.IsEmpty; } 
        }

        // TODO: replace by CharCount, ByteCount
        //[Obsolete("Use GetCharCount(), GetByteCount()")]
        public int Length {
            get { return _content.Count; }
        }
        
        public int GetLength() {
            return _content.Count;
        }

        public void SetLength(int value) {
            ContractUtils.Requires(value >= 0, "value");
            _flags |= CharIndexChangedFlags;
            if (value < _content.Count) {
                _content.Remove(value, _content.Count - value);
            } else {
                _content.Count = value;
            }
        }

        /// <summary>
        /// Returns the number of UTF16 characters.
        /// </summary>
        /// <exception cref="DecoderFallbackException">Invalid characters.</exception>
        public int GetCharCount() {
            return _content.GetCharCount();
        }

        /// <summary>
        /// Returns the number of UTF32 characters.
        /// Each invalid byte sequence is counted as a single character.
        /// </summary>
        public int GetCharacterCount() {
            if (!KnowsCharIndexIsClrIndex && !IsBinary && HasSurrogates()) {
                return GetCharacterCountWithPairs();
            }
            return _content.GetCharacterCount();
        }

        public void SetCharCount(int value) {
            PrepareForCharacterRead().SetLength(value);
        }

        public int GetByteCount() {
            return _content.GetByteCount();
        }

        public void SetByteCount(int value) {
            SwitchToBytes().SetLength(value);
        }

        public MutableString/*!*/ TrimExcess() {
            _content.TrimExcess();
            return this;
        }

        public int Capacity { 
            get {
                return _content.GetCapacity();
            } set {
                _content.SetCapacity(value);
            }
        }

        public void EnsureCapacity(int minCapacity) {
            if (_content.GetCapacity() < minCapacity) {
                _content.SetCapacity(minCapacity);
            }
        }

        #endregion

        #region StartsWith, EndsWith (read-only)

        public bool StartsWith(char value) {
            return _content.StartsWith(value);
        }

        public bool EndsWith(char value) {
            try {
                return GetLastChar() == value;
            } catch (DecoderFallbackException) {
                // The string holds bytes that are not a valid character sequence in its encoding.
                // MRI still answers this question - it looks at the trailing bytes rather than
                // decoding the whole string - and IO#puts asks it about every string it writes,
                // so a string with a stray byte in it must not take the process down.
                // In an ASCII compatible encoding an ASCII character is always encoded as itself,
                // so the last byte decides.
                if (value < 0x80 && _encoding.IsAsciiIdentity) {
                    int byteCount = GetByteCount();
                    return byteCount > 0 && GetByte(byteCount - 1) == (byte)value;
                }
                return false;
            }
        }

        public bool EndsWith(string/*!*/ value) {
            // TODO:
            return _content.ConvertToString().EndsWith(value, StringComparison.Ordinal);
        }

        public bool EndsWith(MutableString/*!*/ value) {
            ContractUtils.RequiresNotNull(value, "value");

            // TODO:
            if (IsBinary || value.IsBinary) {
                int valueLength = value.GetByteCount();
                int offset = GetByteCount() - valueLength;
                if (offset < 0) {
                    return false;
                }

                for (int i = 0; i < valueLength; i++) {
                    if (GetByte(offset + i) != value.GetByte(i)) {
                        return false;
                    }
                }
            } else {
                int valueLength = value.GetCharCount();
                int offset = GetCharCount() - valueLength;
                if (offset < 0) {
                    return false;
                }

                for (int i = 0; i < valueLength; i++) {
                    if (GetChar(offset + i) != value.GetChar(i)) {
                        return false;
                    }
                }
            }

            return true;
        }
        
        #endregion

        #region Enumerations (read-only)

        public struct Character : IEquatable<Character> {
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2105:ArrayFieldsShouldNotBeReadOnly")]
            public readonly byte[] Invalid;
            public readonly char Value;
            public readonly char LowSurrogate;

            public bool IsValid {
                get { return Invalid == null; }
            }

            public bool IsSurrogate {
                get { return LowSurrogate != '\0'; }
            }

            public int Codepoint {
                get { return IsSurrogate ? Tokenizer.ToCodePoint(Value, LowSurrogate) : (int)Value; }
            }

            internal Character(byte[]/*!*/ invalid) {
                Invalid = invalid;
                Value = '\0';
                LowSurrogate = '\0';
            }

            internal Character(char value) {
                Invalid = null;
                Value = value;
                LowSurrogate = '\0';
            }

            internal Character(char highSurrogate, char lowSurrogate) {
                Debug.Assert(Tokenizer.IsHighSurrogate(highSurrogate) && Tokenizer.IsLowSurrogate(lowSurrogate));
                Invalid = null;
                Value = highSurrogate;
                LowSurrogate = lowSurrogate;
            }

            public bool Equals(Character other) {
                if (IsValid) {
                    return other.IsValid && Value == other.Value && LowSurrogate == other.LowSurrogate;
                } else {
                    return !other.IsValid && Invalid.ValueEquals(other.Invalid);
                }
            }

            public MutableString/*!*/ ToMutableString(RubyEncoding/*!*/ encoding) {
                if (IsValid) {
                    return IsSurrogate ?
                        new MutableString(new char[] { Value, LowSurrogate }, encoding) :
                        new MutableString(new char[] { Value }, encoding);
                } else {
                    // copy bytes so that the character remains immutable:
                    return new MutableString(ArrayUtils.Copy(Invalid), encoding);
                }
            }
        }

        public abstract class CharacterEnumerator : IEnumerator<Character> {
            private readonly RubyEncoding/*!*/ _encoding;
            internal int _index;
            internal Character _current;

            protected CharacterEnumerator(RubyEncoding/*!*/ encoding) {
                Assert.NotNull(encoding);
                _encoding = encoding;
                _index = -1;
            }

            public Character Current {
                get {
                    if (_index < 0) {
                        throw new InvalidOperationException();
                    }
                    return _current;
                }
            }

            public virtual void Reset() {
                _index = -1;
                _current = default(Character);
            }

            internal void AppendTo(MutableString/*!*/ str) {
                ContractUtils.Requires(_encoding == str.Encoding);
                if (_index < 0) {
                    _index = 0;
                }

                AppendDataTo(str);

                Reset();
            }

            internal abstract void AppendDataTo(MutableString/*!*/ str);
            public abstract bool MoveNext();
            public abstract bool HasMore { get; }
            
            void IDisposable.Dispose() {
            }

            object System.Collections.IEnumerator.Current {
                get { return _current; }
            }
        }

        internal sealed class StringCharacterEnumerator : CharacterEnumerator {
            private readonly string/*!*/ _data;

            internal StringCharacterEnumerator(RubyEncoding/*!*/ encoding, string/*!*/ data)
                : base(encoding) {
                Assert.NotNull(data);
                _data = data;
            }

            public override bool HasMore {
                get { return _index < _data.Length; }
            }

            public override bool MoveNext() {
                int index = _index;
                if (index < 0) {
                    index = 0;
                }

                if (index == _data.Length) {
                    _index = index;
                    return false;
                }

                char c, d;
                if (Tokenizer.IsHighSurrogate(c = _data[index]) && index + 1 < _data.Length && Tokenizer.IsLowSurrogate(d = _data[index + 1])) {
                    _current = new Character(c, d);
                    _index = index + 2;
#if FEATURE_ENCODING
                } else if (EscapingEncoding.IsEscapedByte(c)) {
                    // A byte that is not valid in the string's encoding, carried through the
                    // character representation as a lone low surrogate. It is a character for
                    // counting and slicing, but it is not a *valid* one, and #codepoints, #ord and
                    // anything else that needs a code point has to say so.
                    _current = new Character(new byte[] { (byte)(c - EscapingEncoding.EscapeBase) });
                    _index = index + 1;
#endif
                } else {
                    _current = new Character(c);
                    _index = index + 1;
                }
                return true;
            }

            internal override void AppendDataTo(MutableString/*!*/ str) {
                str.Append(_data, _index, _data.Length - _index);
            }
        }

        internal sealed class BinaryCharacterEnumerator : CharacterEnumerator {
            private readonly byte[]/*!*/ _data;
            private readonly int _count;

            internal BinaryCharacterEnumerator(RubyEncoding/*!*/ encoding, byte[]/*!*/ data, int count)
                : base(encoding) {
                Assert.NotNull(data);
                _data = data;
                _count = count;
            }

            public override bool HasMore {
                get { return _index < _count; }
            }

            public override bool MoveNext() {
                if (_index < 0) {
                    _index = 0;
                } 
                
                if (!HasMore) {
                    return false;
                }

                _current = new Character((char)_data[_index++]);
                return true;
            }

            internal override void AppendDataTo(MutableString/*!*/ str) {
                str.Append(_data, _index, _count - _index);
            }
        }

        internal sealed class CompositeCharacterEnumerator : CharacterEnumerator {
            private readonly char[]/*!*/ _data;
            private readonly int _count;
            private readonly List<byte[]> _invalid;
#if FEATURE_ENCODING
            private int _invalidIndex;
#endif

            internal CompositeCharacterEnumerator(RubyEncoding/*!*/ encoding, char[]/*!*/ data, int count, List<byte[]> invalid) 
                : base(encoding) {
                _data = data;
                _count = count;
                _invalid = invalid;
            }

            private int InvalidCount {
                get { return _invalid != null ? _invalid.Count : 0; }
            }

            internal override void AppendDataTo(MutableString/*!*/ str) {
#if FEATURE_ENCODING
                int i;
                while (_index < _count && _invalidIndex < InvalidCount) {
                    i = Array.IndexOf(_data, LosslessDecoderFallback.InvalidCharacterPlaceholder, _index);
                    str.Append(_data, _index, i - _index);
                    _index = i + 1;
                    str.Append(_invalid[_invalidIndex++]);
                }
#endif
                str.Append(_data, _index, _count - _index);
            }

            public override bool HasMore {
                get { return _index < _count; }
            }

            public override bool MoveNext() {
                int index = _index;
                if (index < 0) {
                    index = 0;
                }

                if (index == _count) {
                    _index = index;
                    return false;
                }

                char c = _data[index];
#if FEATURE_ENCODING
                if (c != LosslessDecoderFallback.InvalidCharacterPlaceholder) {
#endif
                char d;
                    if (Tokenizer.IsHighSurrogate(c) && index + 1 < _data.Length && Tokenizer.IsLowSurrogate(d = _data[index + 1])) {
                        _current = new Character(c, d);
                        _index = index + 2;
#if FEATURE_ENCODING
                    } else if (EscapingEncoding.IsEscapedByte(c)) {
                        // See StringCharacterEnumerator: a byte that is not valid in the string's
                        // encoding is a character, but not a valid one.
                        _current = new Character(new byte[] { (byte)(c - EscapingEncoding.EscapeBase) });
                        _index = index + 1;
#endif
                    } else {
                        _current = new Character(c);
                        _index = index + 1;
                    }
#if FEATURE_ENCODING
                } else if (_invalidIndex < InvalidCount) {
                    _current = new Character(_invalid[_invalidIndex++]);
                    _index = index + 1;
                } else {
                    // this can only happen if the decoder produces invalid characters \uFFFF, which it should not:
                    throw new InvalidOperationException("Decoder produced an invalid chracter \uFFFF.");
                }
#endif
                return true;
            }

            public override void Reset() {
                base.Reset();
#if FEATURE_ENCODING
                _invalidIndex = -1;
#endif
            }
        }

        internal static CharacterEnumerator/*!*/ EnumerateAsCharacters(byte[]/*!*/ data, int count, RubyEncoding/*!*/ encoding, out char[] allValid) {
#if FEATURE_ENCODING
            Decoder decoder = encoding.Encoding.GetDecoder();
            var fallback = new LosslessDecoderFallback();
            decoder.Fallback = fallback;

            fallback.Track = true;
            char[] chars = new char[decoder.GetCharCount(data, 0, count, true)];

            // TODO: we can use singleton lossless non-tracking decoder for counting characters and tracking for getting the actual chars
            decoder.Reset();
            fallback.Track = false;
            decoder.GetChars(data, 0, count, chars, 0, true);

            allValid = (fallback.InvalidCharacters == null) ? chars : null;
            return new CompositeCharacterEnumerator(encoding, chars, chars.Length, fallback.InvalidCharacters);
#else
            char[] chars = encoding.Encoding.GetChars(data, 0, count);
            allValid = null;
            return new CompositeCharacterEnumerator(encoding, chars, chars.Length, null);
#endif
        }
        
        /// <summary>
        /// Enumerates over characters contained in the string. 
        /// Yields both valid characters and invalid byte sequences.
        /// </summary>
        public CharacterEnumerator/*!*/ GetCharacters() {
            _flags |= CopyOnWriteFlag;
            return _content.GetCharacters();
        }

        public IEnumerable<byte>/*!*/ GetBytes() {
            _flags |= CopyOnWriteFlag;
            return _content.GetBytes(); 
        }

        #endregion

        #region Slices (read-only)

        // converts the string representation to text if not already
        /// <exception cref="IndexOutOfRangeException">Index is out of range.</exception>
        public char GetChar(int index) {
            return _content.GetChar(index);
        }

        // converts the string representation to binary if not already
        /// <exception cref="IndexOutOfRangeException">Index is out of range.</exception>
        public byte GetByte(int index) {
            return _content.GetByte(index);
        }

        // returns -1 if the string is empty
        public int GetLastChar() {
            return (_content.IsEmpty) ? -1 : _content.GetChar(_content.GetCharCount() - 1); 
        }

        // returns -1 if the string is empty
        public int GetFirstChar() {
            return (_content.IsEmpty) ? -1 : _content.GetChar(0);
        }

        /// <summary>
        /// Returns a new mutable string containing a substring of the current one.
        /// </summary>
        public MutableString/*!*/ GetSlice(int start) {
            return GetSlice(start, Int32.MaxValue);
        }

        public MutableString/*!*/ GetSlice(int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            return CreateDerived(_content.GetSlice(start, count), _encoding);
        }

        public string/*!*/ GetStringSlice(int start) {
            return GetStringSlice(start, Int32.MaxValue);
        }

        public string/*!*/ GetStringSlice(int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            return _content.GetStringSlice(start, count);
        }

        public byte[]/*!*/ GetBinarySlice(int start) {
            return GetBinarySlice(start, Int32.MaxValue);
        }

        public byte[]/*!*/ GetBinarySlice(int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            return _content.GetBinarySlice(start, count);
        }

        #endregion

        #region Split (read-only)

        // TODO: binary ops, ...
        public MutableString[]/*!*/ Split(char[]/*!*/ separators, int maxComponents, StringSplitOptions options) {
            // TODO:
            // TODO (encoding):
            return MakeArray(_content.ConvertToString().Split(separators, maxComponents, options), _encoding);
        }
        
        #endregion

        #region IndexOf (read-only)

        public int IndexOf(char value) {
            return IndexOf(value, 0);
        }

        public int IndexOf(char value, int start) {
            return IndexOf(value, start, Int32.MaxValue);
        }

        public int IndexOf(char value, int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            return _content.IndexOf(value, start, count);
        }

        public int IndexOf(byte value) {
            return IndexOf(value, 0);
        }

        public int IndexOf(byte value, int start) {
            return IndexOf(value, start, Int32.MaxValue);
        }

        public int IndexOf(byte value, int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            return _content.IndexOf(value, start, count);
        }

        public int IndexOf(string/*!*/ value) {
            return IndexOf(value, 0);
        }

        public int IndexOf(string/*!*/ value, int start) {
            return IndexOf(value, start, Int32.MaxValue);
        }

        public int IndexOf(string/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            
            return _content.IndexOf(value, start, count);
        }

        public int IndexOf(byte[]/*!*/ value) {
            return IndexOf(value, 0);
        }

        public int IndexOf(byte[]/*!*/ value, int start) {
            return IndexOf(value, start, Int32.MaxValue);
        }

        public int IndexOf(byte[]/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            
            return _content.IndexOf(value, start, count);
        }

        public int IndexOf(MutableString/*!*/ value) {
            return IndexOf(value, 0);
        }

        public int IndexOf(MutableString/*!*/ value, int start) {
            return IndexOf(value, start, Int32.MaxValue);
        }

        public int IndexOf(MutableString/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            
            return value._content.IndexIn(_content, start, count);
        }

        #endregion

        #region LastIndexOf (read-only)

        public int LastIndexOf(char value) {
            return LastIndexOf(value, Int32.MaxValue - 1, Int32.MaxValue);
        }

        public int LastIndexOf(char value, int start) {
            return LastIndexOf(value, start, start + 1);
        }

        public int LastIndexOf(char value, int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0 && count - 1 <= start, "count");
            return _content.LastIndexOf(value, start, count);
        }

        public int LastIndexOf(byte value) {
            return LastIndexOf(value, Int32.MaxValue - 1, Int32.MaxValue);
        }

        public int LastIndexOf(byte value, int start) {
            return LastIndexOf(value, start, start + 1);
        }

        public int LastIndexOf(byte value, int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0 && count - 1 <= start, "count");
            return _content.LastIndexOf(value, start, count);
        }

        public int LastIndexOf(string/*!*/ value) {
            return LastIndexOf(value, Int32.MaxValue - 1, Int32.MaxValue);
        }

        public int LastIndexOf(string/*!*/ value, int start) {
            return LastIndexOf(value, start, start + 1);
        }

        public int LastIndexOf(string/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0 && count - 1 <= start, "count");
            return _content.LastIndexOf(value, start, count);
        }

        public int LastIndexOf(byte[]/*!*/ value) {
            return LastIndexOf(value, Int32.MaxValue - 1, Int32.MaxValue);
        }

        public int LastIndexOf(byte[]/*!*/ value, int start) {
            return LastIndexOf(value, start, start + 1);
        }

        public int LastIndexOf(byte[]/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0 && count - 1 <= start, "count");
            return _content.LastIndexOf(value, start, count);
        }

        public int LastIndexOf(MutableString/*!*/ value) {
            return LastIndexOf(value, Int32.MaxValue - 1, Int32.MaxValue);
        }

        public int LastIndexOf(MutableString/*!*/ value, int start) {
            return LastIndexOf(value, start, start + 1);
        }

        public int LastIndexOf(MutableString/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0 && count - 1 <= start, "count");
            return value._content.LastIndexIn(_content, start, count);
        }

        #endregion

        #region Concat (read-only)

        /// <summary>
        /// Returns a concatenation of this string with other.
        /// </summary>
        public MutableString/*!*/ Concat(MutableString/*!*/ other) {
            ContractUtils.RequiresNotNull(other, "other");
            var encoding = RequireCompatibleEncoding(other);

            // MRI doesn't create a subclass
            return new MutableString(_content.Concat(other._content), encoding);
        }

        #endregion

        #region Append

        /// <summary>
        /// Value should only contain characters that can be represented in the string's encoding.
        /// </summary>
        public MutableString/*!*/ Append(char value) {
            MutateOne(value);
            _content.Append(value, 1);
            return this;
        }

        /// <summary>
        /// Value should only contain characters that can be represented in the string's encoding.
        /// </summary>
        public MutableString/*!*/ Append(char value, int repeatCount) {
            MutateOne(value);
            _content.Append(value, repeatCount);
            return this;
        }

        public MutableString/*!*/ Append(byte value) {
            MutateOne(value);
            _content.Append(value, 1);
            return this;
        }

        public MutableString/*!*/ Append(byte value, int repeatCount) {
            MutateOne(value);
            _content.Append(value, repeatCount);
            return this;
        }

        /// <summary>
        /// Value should only contain characters that can be represented in the string's encoding.
        /// </summary>
        public MutableString/*!*/ Append(char[] value) {
            if (value != null) {
                MutateAppend();
                _content.Append(value, 0, value.Length);
            }
            return this;
        }

        /// <summary>
        /// Value should only contain characters that can be represented in the string's encoding.
        /// </summary>
        public MutableString/*!*/ Append(char[]/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.RequiresArrayRange(value, start, count, "startIndex", "count");

            MutateAppend();
            _content.Append(value, start, count);
            return this;
        }

        /// <summary>
        /// Value should only contain characters that can be represented in the string's encoding.
        /// </summary>
        public MutableString/*!*/ Append(string value) {
            if (value != null) {
                MutateAppend();
                _content.Append(value, 0, value.Length);
            }
            return this;
        }

        /// <summary>
        /// Value should only contain characters that can be represented in the string's encoding.
        /// </summary>
        public MutableString/*!*/ Append(string/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.RequiresArrayRange(value, start, count, "start", "count");
            MutateAppend();

            _content.Append(value, start, count);
            return this;
        }

        public MutableString/*!*/ Append(byte[] value) {
            if (value != null) {
                MutateAppend();
                _content.Append(value, 0, value.Length);
            }
            return this;
        }

        public MutableString/*!*/ Append(byte[]/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.RequiresArrayRange(value, start, count, "start", "count");

            MutateAppend();
            _content.Append(value, start, count);
            return this;
        }

        /// <summary>
        /// Reads at most "count" bytes from "source" stream and appends them to this string.
        /// Allocates space for "count" bytes, so the string might need to be trimmed after the operation.
        /// </summary>
        public MutableString/*!*/ Append(Stream/*!*/ stream, int count) {
            ContractUtils.RequiresNotNull(stream, "stream");
            ContractUtils.Requires(count >= 0, "count");

            MutateAppend();
            _content.Append(stream, count);
            return this;
        }

        public MutableString/*!*/ Append(MutableString value) {
            if ((object)value != null) {
                MutateAppend(value, true);
                _content.Append(value._content, 0, value._content.Count);
            }
            return this;
        }

        public MutableString/*!*/ Append(MutableString/*!*/ value, int start) {
            return Append(value, start, value._content.Count - start);
        }

        /// <summary>
        /// Appends a substring of a given string to this string.
        /// <c>start</c> and <c>count</c> are specified
        /// in characters if the <c>value</c> is represented in characters and 
        /// in bytes if the <c>value</c> is represented in bytes.
        /// </summary>
        public MutableString/*!*/ Append(MutableString/*!*/ value, int start, int count) {
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");

            MutateAppend(value, start == 0 && count == value._content.Count);
            _content.Append(value._content, start, count);
            return this;
        }

        public MutableString/*!*/ AppendMultiple(MutableString/*!*/ value, int repeatCount) {
            ContractUtils.RequiresNotNull(value, "value");
            MutateAppend(value, repeatCount > 0);

            // TODO: we can do better here (double the amount of copied bytes/chars in each iteration)
            var other = value._content;
            EnsureCapacity(other.Count * repeatCount);
            while (repeatCount-- > 0) {
                _content.Append(other, 0, other.Count);
            }
            return this;
        }

        /// <summary>
        /// Format and values should only contain characters that can be represented in the string's encoding.
        /// </summary>
        public MutableString/*!*/ AppendFormat(string/*!*/ format, params object[] args) {
            ContractUtils.RequiresNotNull(format, "format");
            Mutate();

            _content.AppendFormat(CultureInfo.InvariantCulture, format, args);
            return this;
        }

        public MutableString/*!*/ Append(Character character) {
            if (character.IsValid) {
                Append(character.Value);
                if (character.IsSurrogate) {
                    Append(character.LowSurrogate);
                }
                return this;
            } else {
                return Append(character.Invalid);
            }
        }

        public MutableString/*!*/ AppendRemaining(CharacterEnumerator/*!*/ characters) {
            ContractUtils.RequiresNotNull(characters, "characters");
            characters.AppendTo(this);
            return this;
        }

        #endregion

        #region Insert // TODO: Insert(MS) like Append(MS)

        public void SetChar(int index, char value) {
            MutateOne(value);
            _content.SetChar(index, value);
        }

        public void SetByte(int index, byte value) {
            MutateOne(value);
            _content.SetByte(index, value);
        }

        public MutableString/*!*/ Insert(int index, char value) {
            MutateOne(value);
            _content.Insert(index, value);
            return this;
        }

        public MutableString/*!*/ Insert(int index, byte value) {
            MutateOne(value);
            _content.Insert(index, value);
            return this;
        }

        public MutableString/*!*/ Insert(int index, string value) {
            //RequiresArrayInsertIndex(index);
            if (value != null) {
                Mutate();
                _content.Insert(index, value, 0, value.Length);
            }
            return this;
        }

        public MutableString/*!*/ Insert(int index, string/*!*/ value, int start, int count) {
            //RequiresArrayInsertIndex(index);
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.RequiresArrayRange(value, start, count, "start", "count");

            Mutate();
            _content.Insert(index, value, start, count);
            return this;
        }

        public MutableString/*!*/ Insert(int index, byte[] value) {
            //RequiresArrayInsertIndex(index);
            if (value != null) {
                Mutate();
                _content.Insert(index, value, 0, value.Length);
            }
            return this;
        }

        public MutableString/*!*/ Insert(int index, byte[]/*!*/ value, int start, int count) {
            //RequiresArrayInsertIndex(index);
            ContractUtils.RequiresNotNull(value, "value");
            ContractUtils.RequiresArrayRange(value, start, count, "start", "count");

            Mutate();
            _content.Insert(index, value, start, count);
            return this;
        }

        public MutableString/*!*/ Insert(int index, MutableString value) {
            //RequiresArrayInsertIndex(index);
            if (value != null) {
                MutateAppend(value, true);
                value._content.InsertTo(_content, index, 0, value._content.Count);
            }
            return this;
        }

        // TODO: start, count measured in characters or bytes?
        public MutableString/*!*/ Insert(int index, MutableString/*!*/ value, int start, int count) {
            //RequiresArrayInsertIndex(index);
            ContractUtils.RequiresNotNull(value, "value");
            //value.RequiresArrayRange(start, count);

            MutateAppend(value, start == 0 && count == value._content.Count);
            value._content.InsertTo(_content, index, start, count);
            return this;
        }

        #endregion

        #region Reverse

        public MutableString/*!*/ Reverse() {
            MutatePreserveAsciiness();
            PrepareForCharacterWrite();

            var content = _content;

            int length = content.Count;
            if (length <= 1) {
                return this;
            }

            for (int i = 0; i < length / 2; i++) {
                char a = content.GetChar(i);
                char b = content.GetChar(length - i - 1);
                content.SetChar(i, b);
                content.SetChar(length - i - 1, a);
            }

            // A character outside the BMP is one Ruby character but two UTF-16 chars, and the
            // swap above left its halves the wrong way round.  Put each pair back: after the
            // reversal a surrogate pair reads low-then-high, which is exactly the pattern to
            // look for.  An unpaired surrogate has no partner to swap with and stays put.
            for (int i = 0; i < length - 1; i++) {
                if (Char.IsLowSurrogate(content.GetChar(i)) && Char.IsHighSurrogate(content.GetChar(i + 1))) {
                    char low = content.GetChar(i);
                    content.SetChar(i, content.GetChar(i + 1));
                    content.SetChar(i + 1, low);
                    i++;
                }
            }

            Debug.Assert(content == _content);
            return this;
        }

        #endregion

        #region Replace, Write, Remove, Trim, Clear, Translate, TranslateSqueeze, TranslateRemove

        public MutableString/*!*/ Replace(int start, int count, MutableString value) {
            //RequiresArrayRange(start, count);

            // Checked up front, so that an incompatible value leaves the string as it was. Remove
            // and Insert then keep what is known about the ASCII-ness and surrogates of the string
            // (Mutate(value) used to throw both away), so that a String#[]= does not make the next
            // index operation rescan the whole string.
            RequireCompatibleEncoding(value);

            // One BMP character for another leaves every character where it was, so a character
            // index table (MutableString.CharIndex.cs) stays valid: s[i] = "x" in a loop over a
            // string with surrogate pairs would otherwise rebuild it on every assignment.
            var content = _content;
            bool keepCharIndex = (_flags & CharIndexChangedFlags) == 0 && count == 1 && !IsBinary && !value.IsBinary &&
                value._content.Count == 1 && !Char.IsSurrogate(value._content.GetChar(0)) && !Char.IsSurrogate(content.GetChar(start));

            Remove(start, count).Insert(start, value);

            if (keepCharIndex && ReferenceEquals(content, _content)) {
                _flags &= ~CharIndexChangedFlags;
            }
            return this;
        }

        // TODO: characters
        public MutableString/*!*/ WriteBytes(int offset, MutableString/*!*/ value, int start, int count) {
            byte[] bytes = value.GetByteArrayChecked(start, count);
            return Write(offset, bytes, start, count);
        }

        public MutableString/*!*/ Write(int offset, byte[]/*!*/ value, int start, int count) {
            Mutate();
            _content.Write(offset, value, start, count);
            return this;
        }

        public MutableString/*!*/ Write(int offset, byte/*!*/ value, int repeatCount) {
            Mutate();
            _content.Write(offset, value, repeatCount);
            return this;
        }

        public MutableString/*!*/ Remove(int start) {
            ContractUtils.Requires(start >= 0, "start");
            MutateRemove();
            _content.Remove(start, _content.Count - start);
            return this;
        }

        public MutableString/*!*/ Remove(int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            MutateRemove();
            _content.Remove(start, count);
            return this;
        }

        public MutableString/*!*/ Trim(int start, int count) {
            ContractUtils.Requires(start >= 0, "start");
            ContractUtils.Requires(count >= 0, "count");
            MutateRemove();
            _content = _content.GetSlice(start, count);
            return this;
        }

        public MutableString/*!*/ Clear() {
            Mutate();
            _content = _content.GetSlice(0, 0);
            return this;
        }

        private static void PrepareTranslation(MutableString/*!*/ src, MutableString/*!*/ dst, CharacterMap/*!*/ map) {
            ContractUtils.RequiresNotNull(src, "src");
            ContractUtils.RequiresNotNull(dst, "dst");
            ContractUtils.RequiresNotNull(map, "map");
            ContractUtils.Requires(ReferenceEquals(src, dst) || dst.IsEmpty);

            dst.Mutate();
            dst.PrepareForCharacterWrite();

            if (!ReferenceEquals(src, dst)) {
                src.PrepareForCharacterRead();
                dst.SetLength(src.GetLength());
            }
        }

        public static bool Translate(MutableString/*!*/ src, MutableString/*!*/ dst, CharacterMap/*!*/ map) {
            PrepareTranslation(src, dst, map);
            ContractUtils.Requires(map.HasFullMap, "map");

            int srcLength = src.GetCharCount();
            var dstContent = dst._content;
            var srcContent = src._content;

            bool anyMaps = false;
            bool inplace = ReferenceEquals(src, dst);
            
            for (int i = 0; i < srcLength; i++) {
                char s = srcContent.GetChar(i);
                int m = map.TryMap(s);
                if (m >= 0) {
                    anyMaps = true;
                    dstContent.SetChar(i, (char)m);
                } else if (!inplace) {
                    dstContent.SetChar(i, s);
                }
            }

            Debug.Assert(dstContent == dst._content && srcContent == src._content);
            Debug.Assert(!dst.KnowsAscii);
            return anyMaps;
        }

        public static bool TranslateSqueeze(MutableString/*!*/ src, MutableString/*!*/ dst, CharacterMap/*!*/ map) {
            PrepareTranslation(src, dst, map);
            ContractUtils.Requires(map.HasFullMap, "map");

            int srcLength = src.GetCharCount();
            var dstContent = dst._content;
            var srcContent = src._content;

            bool anyMaps = false;
            int j = 0;
            int last = -1;
            for (int i = 0; i < srcLength; i++) {
                char s = srcContent.GetChar(i);
                int m = map.TryMap(s);
                if (m >= 0) {
                    anyMaps = true;
                    if (m != last) {
                        dstContent.SetChar(j++, (char)m);
                    }
                } else {
                    dstContent.SetChar(j++, s);
                }
                last = m;
            }

            if (j < srcLength) {
                dst.Remove(j);
            }

            Debug.Assert(dstContent == dst._content && srcContent == src._content);
            Debug.Assert(!dst.KnowsAscii);
            return anyMaps;
        }

        public static bool TranslateRemove(MutableString/*!*/ src, MutableString/*!*/ dst, CharacterMap/*!*/ map) {
            PrepareTranslation(src, dst, map);
            ContractUtils.Requires(map.HasBitmap, "map");

            var dstContent = dst._content;
            var srcContent = src._content;
            int srcLength = src.GetCharCount();

            bool remove = !map.IsComplemental;
            bool anyMaps = false;
            int j = 0;
            for (int i = 0; i < srcLength; i++) {
                char s = srcContent.GetChar(i);
                if (map.IsMapped(s) == remove) {
                    anyMaps = true;
                } else {
                    dstContent.SetChar(j++, s);
                }
            }

            if (j < srcLength) {
                dst.Remove(j);
            }

            Debug.Assert(dstContent == dst._content && srcContent == src._content);
            Debug.Assert(!dst.KnowsAscii);
            return anyMaps;
        }

        #endregion

        #region Quoted Representation (read-only)

#if FEATURE_ENCODING
        private sealed class DumpDecoderFallback : DecoderFallback {
            // \xXX
            // \000
            private const int ReplacementLength = 4;

            // We can't emit backslash directly since it would be escaped by subsequent processing.
            internal const char EscapePlaceholder = '\uffff';

            private readonly bool _octalEscapes;

            public DumpDecoderFallback(bool octalEscapes) {
                _octalEscapes = octalEscapes;
            }

            public override DecoderFallbackBuffer/*!*/ CreateFallbackBuffer() {
                return new Buffer(this);
            }

            public override int MaxCharCount {
                get { return ReplacementLength; }
            }

            internal sealed class Buffer : DecoderFallbackBuffer {
                private readonly DumpDecoderFallback _fallback;
                private int _index;
                private byte[] _bytes;

                public Buffer(DumpDecoderFallback/*!*/ fallback) {
                    _fallback = fallback;
                }

                public bool HasInvalidCharacters {
                    get { return _bytes != null; }
                }

                public override bool Fallback(byte[]/*!*/ bytesUnknown, int index) {
                    _bytes = bytesUnknown;
                    _index = 0;
                    return true;
                }

                public override char GetNextChar() {
                    if (Remaining == 0) {
                        return '\0';
                    }

                    int state = _index % ReplacementLength;
                    int b = _bytes[_index / ReplacementLength];
                    _index++;

                    if (_fallback._octalEscapes) {
                        switch (state) {
                            case 0: return EscapePlaceholder;
                            case 1: return (char)('0' + (b >> 6));
                            case 2: return (char)('0' + ((b >> 3) & 7));
                            case 3: return (char)('0' + (b & 7));
                        }
                    } else {
                        switch (state) {
                            case 0: return EscapePlaceholder;
                            case 1: return 'x';
                            case 2: return (b >> 4).ToUpperHexDigit();
                            case 3: return (b & 0xf).ToUpperHexDigit();
                        }
                    }

                    throw Assert.Unreachable;
                }

                public override bool MovePrevious() {
                    if (_index == 0) {
                        return false;
                    }
                    _index--;
                    return true;
                }

                public override int Remaining {
                    get { return _bytes.Length * ReplacementLength - _index; }
                }

                public override void Reset() {
                    _index = 0;
                }
            }
        }

        private static string/*!*/ ToStringWithEscapedInvalidCharacters(byte[]/*!*/ bytes, Encoding/*!*/ encoding, bool octalEscapes, out int escapePlaceholder) {
            var decoder = encoding.GetDecoder();
            decoder.Fallback = new DumpDecoderFallback(octalEscapes);
            char[] chars = new char[decoder.GetCharCount(bytes, 0, bytes.Length, true)];
            decoder.GetChars(bytes, 0, bytes.Length, chars, 0, true);
            escapePlaceholder = ((DumpDecoderFallback.Buffer)decoder.FallbackBuffer).HasInvalidCharacters ? DumpDecoderFallback.EscapePlaceholder : -1;
            return new String(chars);
        }
#else
        private static string/*!*/ ToStringWithEscapedInvalidCharacters(byte[]/*!*/ bytes, Encoding/*!*/ encoding, bool octalEscapes, out int escapePlaceholder) {
            // fallback not supported, just replace invalid characters with the default replacement:
            escapePlaceholder = -1;
            return new String(encoding.GetChars(bytes));
        }
#endif

        // TODO: make struct and include quote character
        [Flags]
        public enum Escape {
            Default = 0,

            /// <summary>
            /// Escape all non-ASCII characters.
            /// </summary>
            NonAscii = 1,

            /// <summary>
            /// Escape #{, #$, #@ and \.
            /// </summary>
            Special = 2,

            /// <summary>
            /// Use octal escapes. Hexadecimal are used if not specified.
            /// </summary>
            Octal = 4
        }

        private static void AppendBinaryCharRepresentation(StringBuilder/*!*/ result, int currentChar, int nextChar, Escape escape, int quote) {

            Debug.Assert(currentChar >= 0 && currentChar <= 0x00ff);
            switch (currentChar) {
                case '\a': result.Append("\\a"); break;
                case '\b': result.Append("\\b"); break;
                case '\t': result.Append("\\t"); break;
                case '\n': result.Append("\\n"); break;
                case '\v': result.Append("\\v"); break;
                case '\f': result.Append("\\f"); break;
                case '\r': result.Append("\\r"); break;
                case 27: result.Append("\\e"); break;
                case '\\':
                    if ((escape & Escape.Special) != 0) {
                        result.Append("\\\\");
                    } else {
                        result.Append('\\');
                    }
                    return;

                case '#':
                    if ((escape & Escape.Special) != 0) {
                        switch (nextChar) {
                            case '{':
                            case '$':
                            case '@':
                                result.Append('\\');
                                break;
                        }
                    }
                    result.Append('#');
                    break;

                default:
                    if (currentChar == quote) {
                        result.Append('\\');
                        result.Append((char)quote);
                    } else if (currentChar < 0x0020 || currentChar == 0x007f
                        || currentChar >= 0x080 && (escape & Escape.NonAscii) != 0) {
                        // DEL is a control character: MRI escapes it like the other
                        // non-printable ASCII characters rather than writing it out raw.
                        AppendHexEscape(result, currentChar);
                    } else {
                        result.Append((char)currentChar);
                    }
                    break;
            }
        }

        public static int AppendUnicodeCharRepresentation(StringBuilder/*!*/ result, int currentChar, int nextChar, Escape escape, 
            int quote, int escapePlaceholder) {

            int inc = 1;
            if (currentChar == escapePlaceholder) {
                result.Append('\\');
            } else if (currentChar < 0x0080) {
                if (IsUnnamedControlCharacter(currentChar) && currentChar != quote) {
                    // #inspect spells a control character with no single-letter escape as
                    // \uXXXX; #dump - which is the escape-everything mode - uses \xXX.
                    if ((escape & Escape.NonAscii) != 0) {
                        AppendHexEscape(result, currentChar);
                    } else {
                        AppendUnicodeEscape(result, currentChar);
                    }
                } else {
                    AppendBinaryCharRepresentation(result, currentChar, nextChar, escape, quote);
                }
            } else if (IsEscapedByte(currentChar)) {
                // a byte that did not decode (see EscapingEncoding) is shown as the byte it is
                AppendHexEscape(result, currentChar - EscapingEncoding.EscapeBase);
            } else if ((escape & Escape.NonAscii) != 0) {
                if (nextChar != -1 && Char.IsSurrogatePair((char)currentChar, (char)nextChar)) {
                    currentChar = Tokenizer.ToCodePoint(currentChar, nextChar);
                    inc = 2;
                }
                AppendCodepointEscape(result, currentChar);
            } else if (nextChar != -1 && Char.IsSurrogatePair((char)currentChar, (char)nextChar)) {
                int codepoint = Tokenizer.ToCodePoint(currentChar, nextChar);
                inc = 2;
                if (IsPrintableCodepoint(codepoint)) {
                    result.Append((char)currentChar);
                    result.Append((char)nextChar);
                } else {
                    AppendCodepointEscape(result, codepoint);
                }
            } else if (Char.IsSurrogate((char)currentChar)) {
                // we have to escape - the character is incomplete:
                result.Append("\\u{");
                result.Append(Convert.ToString(currentChar, 16));
                result.Append('}');
            } else if (!IsPrintableCodepoint(currentChar)) {
                // MRI's #inspect escapes anything Onigmo does not call printable, which is
                // how "\u0080".inspect is "\"\\u0080\"" and not an invisible byte.
                AppendCodepointEscape(result, currentChar);
            } else {
                result.Append((char)currentChar);
            }
            return inc;
        }

        /// <summary>A lone low surrogate in U+DC80..U+DCFF stands for one undecodable byte.</summary>
        private static bool IsEscapedByte(int c) {
            return c >= EscapingEncoding.EscapeBase + 0x80 && c <= EscapingEncoding.EscapeBase + 0xFF;
        }

        /// <summary>\uXXXX up to U+FFFF, \u{XXXXXX} above it - MRI writes both in upper case.</summary>
        private static void AppendCodepointEscape(StringBuilder/*!*/ result, int c) {
            if (c <= 0xFFFF) {
                AppendUnicodeEscape(result, c);
            } else {
                result.Append("\\u{");
                result.Append(Convert.ToString(c, 16).ToUpperInvariant());
                result.Append('}');
            }
        }

        /// <summary>Approximates Onigmo's ONIGENC_IS_CODE_PRINT for Unicode.</summary>
        private static bool IsPrintableCodepoint(int c) {
            if (c < 0 || c > 0x10FFFF) {
                return false;
            }
            UnicodeCategory category = (c <= 0xFFFF)
                ? CharUnicodeInfo.GetUnicodeCategory((char)c)
                : CharUnicodeInfo.GetUnicodeCategory(Char.ConvertFromUtf32(c), 0);

            // Cf (soft hyphen, ZWSP, BOM, ...) and Co (private use) are printable to MRI;
            // Cc, Cs, Cn, Zl and Zp are not.
            switch (category) {
                case UnicodeCategory.Control:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return false;
                default:
                    return true;
            }
        }

        public static void AppendCharRepresentation(StringBuilder/*!*/ result, int currentChar, int nextChar, Escape escape, 
            int quote, int escapePlaceholder) {

            if (currentChar == escapePlaceholder) {
                result.Append('\\');
            } else if (currentChar < 0x0100) {
                AppendBinaryCharRepresentation(result, currentChar, nextChar, escape, quote);
            } else {
                result.Append((char)currentChar);
            }
        }

        private static void AppendHexEscape(StringBuilder/*!*/ result, int c) {
            result.Append("\\x");
            result.Append((c >> 4).ToUpperHexDigit());
            result.Append((c & 0xf).ToUpperHexDigit());
        }

        private static void AppendUnicodeEscape(StringBuilder/*!*/ result, int c) {
            result.Append("\\u");
            result.Append((c >> 12 & 0xf).ToUpperHexDigit());
            result.Append((c >> 8 & 0xf).ToUpperHexDigit());
            result.Append((c >> 4 & 0xf).ToUpperHexDigit());
            result.Append((c & 0xf).ToUpperHexDigit());
        }

        /// <summary>
        /// True for the ASCII control characters that have no single-letter escape sequence
        /// (\a \b \t \n \v \f \r \e), including DEL.
        /// </summary>
        private static bool IsUnnamedControlCharacter(int c) {
            switch (c) {
                case '\a': case '\b': case '\t': case '\n':
                case '\v': case '\f': case '\r': case 27:
                    return false;
                default:
                    return c < 0x0020 || c == 0x007f;
            }
        }

        private string/*!*/ ToStringWithEscapedInvalidCharacters(RubyEncoding/*!*/ encoding, bool octalEscapes, out int escapePlaceholder) {
            Debug.Assert(encoding != RubyEncoding.Binary);
            if (IsBinary || encoding != _encoding) {
                return ToStringWithEscapedInvalidCharacters(ToByteArray(), encoding.Encoding, octalEscapes, out escapePlaceholder);
            } else {
                escapePlaceholder = -1;
                return ToString();
            }
        }

        /// <summary>
        /// Returns a string with all non-ASCII characters replaced by escaped Unicode or hexadecimal numeric sequences.
        /// </summary>
        public string/*!*/ ToAsciiString() {
            var result = AppendRepresentation(new StringBuilder(), null, Escape.NonAscii, -1).ToString();
            Debug.Assert(result.IsAscii());
            return result;
        }

        /// <summary>
        /// Returns a copy of the content in a form of a read-only UTF16 string with escaped invalid characters.
        /// </summary>
        public string/*!*/ ToStringWithEscapedInvalidCharacters(RubyEncoding/*!*/ encoding) {
            ContractUtils.RequiresNotNull(encoding, "encoding");
            return AppendRepresentation(new StringBuilder(), encoding, Escape.Default, -1).ToString();
        }

        public StringBuilder/*!*/ AppendRepresentation(StringBuilder/*!*/ result, RubyEncoding forceEncoding, Escape escape, int quote) {
            ContractUtils.RequiresNotNull(result, "result");

            RubyEncoding encoding = forceEncoding ?? _encoding;

            if (encoding == RubyEncoding.Binary || ((escape & Escape.NonAscii) != 0 && encoding != RubyEncoding.UTF8)) {
                escape |= Escape.NonAscii;
                if (IsBinary) {
                    AppendBinaryRepresentation(result, ToByteArray(), escape, quote);
                } else {
                    AppendStringRepresentation(result, ToString(), escape, quote, -1);
                }
            } else {
                int escapePlaceholder;
                var str = ToStringWithEscapedInvalidCharacters(encoding, (escape & Escape.Octal) != 0, out escapePlaceholder);

                if (encoding == RubyEncoding.UTF8) {
                    AppendUnicodeRepresentation(result, str, escape, quote, escapePlaceholder);
                } else {
                    AppendStringRepresentation(result, str, escape, quote, escapePlaceholder);
                }
            }
            
            return result;
        }

        public static StringBuilder/*!*/ AppendUnicodeRepresentation(StringBuilder/*!*/ result, string/*!*/ str, Escape escape,
            int quote, int escapePlaceholder) {

            int i = 0;
            while (i < str.Length) {
                i += AppendUnicodeCharRepresentation(result, (int)str[i], (i < str.Length - 1) ? (int)str[i + 1] : -1, escape, quote, escapePlaceholder);
            }

            return result;
        }

        public static StringBuilder/*!*/ AppendStringRepresentation(StringBuilder/*!*/ result, string/*!*/ str, Escape escape,
            int quote, int escapePlaceholder) {
            for (int i = 0; i < str.Length; i++) {
                AppendCharRepresentation(result, (int)str[i], (i < str.Length - 1) ? (int)str[i + 1] : -1, escape, quote, escapePlaceholder);
            }
            return result;
        }

        public static StringBuilder/*!*/ AppendBinaryRepresentation(StringBuilder/*!*/ result, byte[]/*!*/ bytes, Escape escape, int quote) {
            for (int i = 0; i < bytes.Length; i++) {
                AppendCharRepresentation(result, (int)bytes[i], (i < bytes.Length - 1) ? (int)bytes[i + 1] : -1, escape, quote, -1);
            }
            return result;
        }

        internal string/*!*/ GetDebugValue() {
            return AppendRepresentation(new StringBuilder(), null, MutableString.Escape.Default, '"').ToString();
        }

        internal string/*!*/ GetDebugType() {
            if (!IsBinary) {
                return "String (" + _encoding.ToString() + ")";
            } else if (_encoding != RubyEncoding.Binary) {
                return "String (binary/" + _encoding.ToString() + ")";
            } else {
                return "String (binary)";
            }
        }

        #endregion

        #region FormatMessage (read-only)

        /// <summary>
        /// Formats an error message that can be loaded from resources and thus localized.
        /// </summary>
        public static MutableString/*!*/ FormatMessage(string/*!*/ message, params MutableString[]/*!*/ args) {
            return MutableString.Create(String.Format(message, args), RubyEncoding.UTF8);
        }

        #endregion

        #region Internal Helpers

        internal byte[]/*!*/ GetByteArray(out int count) {
            return _content.GetByteArray(out count);
        }

        internal byte[]/*!*/ GetByteArrayChecked(int start, int count) {
            int byteCount;
            var result = _content.GetByteArray(out byteCount);
            if (count < 0 || start > byteCount - count) {
                throw new ArgumentOutOfRangeException("count");
            }
            return result;
        }

        #endregion
    }
}

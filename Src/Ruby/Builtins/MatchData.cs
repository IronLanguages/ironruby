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

using Microsoft.Scripting.Utils;
using System.Text.RegularExpressions;
using IronRuby.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace IronRuby.Builtins {
    public partial class MatchData : IDuplicable, IRubyObjectState {
        private const int FrozenFlag = 1;
        private const int TaintedFlag = 2;
        private const int UntrustedFlag = 4;

        private int _flags;
        private Match/*!*/ _match;
        private MutableString/*!*/ _originalString;

        // Set only when the regex ran against a k-coded (/u /e /s /n) decoding of the input.
        // In that case _originalString is still in byte representation, while the CLR Match
        // reports offsets into _encodedInput (UTF-16 code units), so the two index spaces
        // disagree and every offset has to be translated. Null means the two agree.
        private string _encodedInput;
        private Encoding _encoding;
        private int _startByteOffset;
        private int _startCharOffset;

        // The text the CLR regex ran on, when it holds a surrogate pair - which makes its CLR
        // offsets differ from Ruby's character offsets. Null otherwise (nearly always), and then
        // the two agree. It is the regex's own immutable input, not _originalString, so the
        // translation neither depends on _originalString's representation nor on a clone of it
        // having to work out its character index again.
        private string _pairedText;

        // The pattern that produced the match. MatchData#regexp has to answer the very object
        // the caller matched with - the spec compares object_id - so it cannot be rebuilt from
        // the CLR Regex and has to be carried here from every construction site.
        private RubyRegex _regexp;

        // #string answers the same frozen String every time it is called, so it is made once.
        private MutableString _frozenInput;

        // GroupCount, worked out once per match: it is asked for by every group access, and
        // telling a hidden group from a Ruby one parses group names. Zero means not known yet
        // (there is always group 0).
        private int _groupCount;

        public MutableString/*!*/ OriginalString { get { return _originalString; } }

        public RubyRegex Regexp { get { return _regexp; } }

        public MutableString/*!*/ FrozenOriginalString {
            get { return _frozenInput ?? (_frozenInput = _originalString.CreateFrozenSnapshot()); }
        }

        /// <summary>
        /// The encoding of the match data is always the same as encoding of the input string 
        /// (even if the regex has a different compatible encoding).
        /// </summary>
        public RubyEncoding/*!*/ Encoding { get { return _originalString.Encoding; } }

        /// <summary>
        /// The number of Ruby groups, group 0 included. The transformed pattern may also have
        /// hidden groups (RegexpTransformer.HiddenGroupBase); those are numbered last, far above
        /// the Ruby groups, so the numbers from Groups.Count down that are not Ruby groups are
        /// ones no group has (an empty name) or hidden ones.
        /// </summary>
        public int GroupCount { 
            get {
                int count = _groupCount;
                if (count > 0) {
                    return count;
                }
                var groups = _match.Groups;
                count = groups.Count;
                while (count > 1 && IsHiddenGroup(groups[count - 1])) {
                    count--;
                }
                return _groupCount = count;
            } 
        }

        private static bool IsHiddenGroup(Group/*!*/ group) {
            int number;
            return group.Name.Length == 0 || Int32.TryParse(group.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number)
                && RegexpTransformer.IsHiddenGroupNumber(number);
        }

        public int Index {
            get { return GetGroupStart(0); } 
        }

        public int Length {
            get { return GetGroupLength(0); } 
        }

        /// <summary>
        /// Where the match starts as Ruby counts: in characters. <see cref="Index"/> is the CLR
        /// index into the subject, for slicing it; this is the one to hand to Ruby code.
        /// </summary>
        public int CharacterIndex {
            get { return ToCharIndex(_match.Index); }
        }

        /// <summary>Where the match ends, in characters.</summary>
        public int CharacterEnd {
            get { return ToCharIndex(_match.Index + _match.Length); }
        }

        #region Construction

        private MatchData(Match/*!*/ match, MutableString/*!*/ originalString) {
            Debug.Assert(match.Success);

            _match = match;

            // TODO (opt): create groups instead?
            _originalString = originalString;

            IsTainted = originalString.IsTainted;
            IsUntrusted = originalString.IsUntrusted;
        }

        private MatchData(Match/*!*/ match, MutableString/*!*/ originalString, string encodedInput, Encoding encoding, int startByteOffset)
            : this(match, originalString) {
            _encodedInput = encodedInput;
            _encoding = encoding;
            _startByteOffset = startByteOffset;
            if (startByteOffset > 0) {
                int byteCount;
                byte[] bytes = originalString.GetByteArray(out byteCount);
                int prefixBytes = Math.Min(startByteOffset, byteCount);
                if (encoding.IsSingleByte) {
                    _startCharOffset = prefixBytes;
                } else {
                    // characters, not UTF-16 code units: a pair is one
                    _startCharOffset = encoding.GetString(bytes, 0, prefixBytes).GetCharacterCount();
                }
            }
        }

        public MatchData() {
            _originalString = MutableString.FrozenEmpty;
            _match = Match.Empty;
        }

        protected MatchData(MatchData/*!*/ data)
            : this(data._match, data._originalString) {
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = CreateInstance();
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        protected virtual MatchData/*!*/ CreateInstance() {
            return new MatchData();
        }
        
        public void InitializeFrom(MatchData/*!*/ other) {
            _match = other._match;
            _groupCount = 0;
            _originalString = other._originalString;
            _encodedInput = other._encodedInput;
            _encoding = other._encoding;
            _startByteOffset = other._startByteOffset;
            _startCharOffset = other._startCharOffset;
            _pairedText = other._pairedText;
            _regexp = other._regexp;
            _frozenInput = other._frozenInput;
        }

        /// <summary>
        /// <paramref name="kcode"/> is non-null exactly when the regex was run against a k-coded
        /// (/u /e /s /n) decoding of <paramref name="input"/> rather than against the input's own
        /// character representation. The CLR offsets then index <paramref name="encodedInput"/>
        /// while <paramref name="input"/> is still addressed by byte, so they must be translated.
        /// </summary>
        internal static MatchData Create(Match/*!*/ match, MutableString/*!*/ input, bool freezeInput, string/*!*/ encodedInput,
            RubyEncoding kcode, int startByteOffset, RubyRegex regexp, bool hasPairs) {

            if (!match.Success) {
                return null;
            }

            if (freezeInput) {
                input = input.CreateFrozenSnapshot();
            }

            MatchData result = (kcode == null || encodedInput == null)
                ? new MatchData(match, input)
                : new MatchData(match, input, encodedInput, kcode.Encoding, startByteOffset);
            result._regexp = regexp;
            if (hasPairs) {
                result._pairedText = encodedInput;
            }
            return result;
        }

        #endregion

        #region Index translation

        /// <summary>
        /// Maps a CLR offset (into the string the Regex actually ran on) onto the index space
        /// _originalString is addressed by. Identity unless a k-code decoding took place.
        /// </summary>
        private int ToOriginalIndex(int clrIndex) {
            if (_encodedInput == null) {
                return clrIndex;
            }
            if (clrIndex <= 0) {
                return _startByteOffset;
            }
            if (_encoding.IsSingleByte) {
                // One byte per character, so the offsets already agree. Counting the bytes of the
                // prefix instead would make every group offset a pass over everything before it.
                return _startByteOffset + Math.Min(clrIndex, _encodedInput.Length);
            }
            if (clrIndex >= _encodedInput.Length) {
                return _startByteOffset + _encoding.GetByteCount(_encodedInput);
            }
            return _startByteOffset + _encoding.GetByteCount(_encodedInput.AsSpan(0, clrIndex));
        }

        private int ToOriginalLength(int clrIndex, int clrLength) {
            if (_encodedInput == null) {
                return clrLength;
            }
            return ToOriginalIndex(clrIndex + clrLength) - ToOriginalIndex(clrIndex);
        }

        /// <summary>
        /// The Ruby character offset a CLR offset stands for. Without a k-code decoding the regex
        /// ran on the string's own character representation, and the string translates it (the
        /// identity unless it holds a surrogate pair); with one, the CLR offset counts UTF-16
        /// units of the decoded input, only from the position the match started at.
        /// </summary>
        private int ToCharIndex(int clrIndex) {
            if (_encodedInput == null) {
                return (_pairedText == null) ? clrIndex : MutableString.ToCharacterIndex(_pairedText, clrIndex);
            }
            int units = Math.Min(Math.Max(clrIndex, 0), _encodedInput.Length);
            if (_pairedText != null) {
                units = MutableString.ToCharacterIndex(_pairedText, units);
            }
            return _startCharOffset + units;
        }

        /// <summary>
        /// The byte offset a CLR offset stands for. With a k-code decoding that is what
        /// ToOriginalIndex already answers; without one the CLR offset counts characters, so the
        /// bytes they occupy in the subject have to be counted out.
        /// </summary>
        private int ToByteIndex(int clrIndex) {
            if (_encodedInput != null) {
                return ToOriginalIndex(clrIndex);
            }
            if (clrIndex <= 0) {
                return 0;
            }
            if (_originalString.DetectSingleByteCharacters()) {
                // One byte per character, so the CLR offset already is the byte offset. Worth the
                // check: the general path below copies the whole prefix out to count its bytes,
                // which turns every match on a long subject into a pass over it.
                return Math.Min(clrIndex, _originalString.GetByteCount());
            }
            return _originalString.GetSlice(0, Math.Min(clrIndex, _originalString.GetCharCount())).GetByteCount();
        }

        /// <summary>
        /// The [start, end] offsets of a group, in characters or in bytes, or null when the group
        /// did not take part in the match. <paramref name="groupIndex"/> is checked against the
        /// pattern's group count.
        /// </summary>
        public int[] GetGroupOffsets(int groupIndex, bool inBytes) {
            RequireExistingGroup(groupIndex);
            return OffsetsOf(_match.Groups[groupIndex], inBytes);
        }

        /// <summary>
        /// What <see cref="GetGroupOffsets(int, bool)"/> answers in bytes, without an array: false
        /// when the group did not take part in the match. For a scanner that asks for an offset
        /// pair on every token.
        /// </summary>
        public bool TryGetGroupByteOffsets(int groupIndex, out int start, out int end) {
            if (groupIndex != 0) { // group 0 always exists
                RequireExistingGroup(groupIndex);
            }
            // group 0 is the Match itself, which spares the group collection
            Group group = (groupIndex == 0) ? _match : _match.Groups[groupIndex];
            if (!group.Success) {
                start = end = 0;
                return false;
            }
            start = ToByteIndex(group.Index);
            end = ToByteIndex(group.Index + group.Length);
            return true;
        }

        public int[] GetGroupOffsets(string/*!*/ name, bool inBytes) {
            if (!HasNamedGroup(name)) {
                throw RubyExceptions.CreateIndexError("undefined group name reference: {0}", name);
            }
            return OffsetsOf(GetNamedGroup(name), inBytes);
        }

        /// <summary>
        /// The group a name stands for. When several groups share it (see RegexpTransformer),
        /// that is the last of them that took part in the match, as in Onigmo.
        /// </summary>
        private Group/*!*/ GetNamedGroup(string/*!*/ name) {
            Group result = null;
            foreach (var clrName in _match.Groups.Keys) {
                if (RegexpTransformer.GetRubyGroupName(clrName) == name) {
                    var group = _match.Groups[clrName];
                    if (result == null || group.Success) {
                        result = group;
                    }
                }
            }
            return result ?? _match.Groups[name];
        }

        private int[] OffsetsOf(Group/*!*/ group, bool inBytes) {
            if (!group.Success) {
                return null;
            }
            return inBytes
                ? new[] { ToByteIndex(group.Index), ToByteIndex(group.Index + group.Length) }
                : new[] { ToCharIndex(group.Index), ToCharIndex(group.Index + group.Length) };
        }

        #endregion

        #region IRubyObjectState Members

        public bool IsFrozen {
            get { return (_flags & FrozenFlag) != 0; }
        }

        public bool IsTainted {
            get { return (_flags & TaintedFlag) != 0; }
            set { _flags = (_flags & ~TaintedFlag) | (value ? TaintedFlag : 0); }
        }

        public bool IsUntrusted {
            get { return (_flags & UntrustedFlag) != 0; }
            set { _flags = (_flags & ~UntrustedFlag) | (value ? UntrustedFlag : 0); }
        }

        public void Freeze() {
            _flags |= FrozenFlag;
        }

        #endregion

        public bool GroupSuccess(int index) {
            return _match.Groups[index].Success;
        }

        public MutableString GetValue() {
            return _match.Success ? _originalString.GetSlice(Index, Length).TaintBy(this) : null;
        }

        /// <summary>
        /// A single Integer subscript - MatchData#[n] and Regexp.last_match(n) - counts a negative
        /// index back from the last group, and unlike the two-argument and Range forms it never
        /// reaches group 0: MRI answers nil as soon as the index walks past group 1.
        /// </summary>
        public MutableString GetNthGroupValue(int index) {
            if (index < 0) {
                index += GroupCount;
                if (index < 1) {
                    return null;
                }
            }
            return GetGroupValue(index);
        }

        public MutableString GetGroupValue(int index) {
            // we don't need to check index range, Groups indexer returns an unsuccessful group if out of range:
            return GroupSuccess(index) ? _originalString.GetSlice(GetGroupStart(index), GetGroupLength(index)).TaintBy(this) : null;
        }

        public MutableString AppendGroupValue(int index, MutableString/*!*/ result) {
            // we don't need to check index range, Groups indexer returns an unsuccessful group if out of range:
            return GroupSuccess(index) ? result.Append(_originalString, GetGroupStart(index), GetGroupLength(index)).TaintBy(this) : null;
        }

        /// <summary>
        /// The names of the pattern's named groups, in the order the pattern declares them.
        /// The CLR reports the numbered groups through the same collection, so those are
        /// filtered out here. Not a [RubyMethod]: MatchData's named-group methods are
        /// written in Ruby in the prelude and reach these two as plain CLR methods.
        /// </summary>
        public string/*!*/[]/*!*/ GetGroupNames() {
            var names = _match.Groups.Keys;
            var result = new List<string>();
            foreach (var name in names) {
                var rubyName = RegexpTransformer.GetRubyGroupName(name);
                if (rubyName != null && !result.Contains(rubyName)) {
                    result.Add(rubyName);
                }
            }
            return result.ToArray();
        }

        /// <summary>The group's name if it has one, otherwise its number as a string.</summary>
        public string/*!*/ GetGroupName(int index) {
            string name = _match.Groups[index].Name;
            return RegexpTransformer.GetRubyGroupName(name) ?? name;
        }

        public bool HasNamedGroup(string/*!*/ name) {
            foreach (var groupName in _match.Groups.Keys) {
                if (RegexpTransformer.GetRubyGroupName(groupName) == name) {
                    return true;
                }
            }
            return false;
        }

        public bool NamedGroupSuccess(string/*!*/ name) {
            return HasNamedGroup(name) && GetNamedGroup(name).Success;
        }

        /// <summary>Character index where a named group matched, or -1.</summary>
        public int GetNamedGroupStart(string/*!*/ name) {
            return NamedGroupSuccess(name) ? ToOriginalIndex(GetNamedGroup(name).Index) : -1;
        }

        public int GetNamedGroupLength(string/*!*/ name) {
            if (!NamedGroupSuccess(name)) {
                return -1;
            }
            var group = GetNamedGroup(name);
            return ToOriginalLength(group.Index, group.Length);
        }

        public MutableString GetNamedGroupValue(string/*!*/ name) {
            return NamedGroupSuccess(name)
                ? _originalString.GetSlice(GetNamedGroupStart(name), GetNamedGroupLength(name)).TaintBy(this)
                : null;
        }

        public int GetGroupStart(int groupIndex) {
            ContractUtils.Requires(groupIndex >= 0);
            return ToOriginalIndex(_match.Groups[groupIndex].Index);
        }

        public int GetGroupLength(int groupIndex) {
            ContractUtils.Requires(groupIndex >= 0);
            var group = _match.Groups[groupIndex];
            return ToOriginalLength(group.Index, group.Length);
        }
        
        public int GetGroupEnd(int groupIndex) {
            ContractUtils.Requires(groupIndex >= 0);
            return GetGroupStart(groupIndex) + GetGroupLength(groupIndex);
        }

        public void RequireExistingGroup(int index) {
            if (index >= GroupCount || index < 0) {
                throw RubyExceptions.CreateIndexError("index {0} out of matches", index);
            }
        }

        public MutableString/*!*/ GetPreMatch() {
            return _originalString.GetSlice(0, GetGroupStart(0)).TaintBy(this);
        }
        
        public MutableString/*!*/ GetPostMatch() {
            return _originalString.GetSlice(GetGroupEnd(0)).TaintBy(this);
        }
    }
}

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
using System.Diagnostics;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    [RubyClass("MatchData", Extends = typeof(MatchData), Inherits = typeof(Object))]
    [UndefineMethod("new", IsStatic = true)]
    [UndefineMethod("allocate", IsStatic = true)]
    public static class MatchDataOps {

        #region Private Instance Methods

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static MatchData/*!*/ InitializeCopy(MatchData/*!*/ self, [NotNull]MatchData/*!*/ other) {
            self.InitializeFrom(other);
            return self;
        }

        #endregion

        #region Public Instance Methods

        [RubyMethod("[]")]
        public static MutableString GetGroup(MatchData/*!*/ self, [DefaultProtocol]int index) {
            return self.GetNthGroupValue(index);
        }

        [RubyMethod("[]")]
        public static RubyArray GetGroup(MatchData/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int length) {
            if (!IListOps.NormalizeRange(self.GroupCount, ref start, ref length)) {
                return null;
            }

            RubyArray result = new RubyArray();
            for (int i = 0; i < length; i++) {
                result.Add(self.GetGroupValue(start + i));
            }

            return result;
        }

        [RubyMethod("[]")]
        public static RubyArray GetGroup(ConversionStorage<int>/*!*/ fixnumCast, MatchData/*!*/ self, [NotNull]Range/*!*/ range) {
            int begin, count;
            if (!IListOps.NormalizeRange(fixnumCast, self.GroupCount, range, out begin, out count)) {
                return null;
            }
            return GetGroup(self, begin, count);
        }

        // #begin, #end and #offset report character offsets, #bytebegin, #byteend and
        // #byteoffset byte offsets; the two differ as soon as the subject is not ASCII. Each
        // takes either a group number or - since 1.9 - a group name.
        #region begin, end, offset, bytebegin, byteend, byteoffset

        private static object Bound(int[] offsets, int which) {
            return offsets == null ? null : ScriptingRuntimeHelpers.Int32ToObject(offsets[which]);
        }

        private static RubyArray/*!*/ BoundPair(int[] offsets) {
            RubyArray result = new RubyArray(2);
            result.Add(offsets == null ? null : ScriptingRuntimeHelpers.Int32ToObject(offsets[0]));
            result.Add(offsets == null ? null : ScriptingRuntimeHelpers.Int32ToObject(offsets[1]));
            return result;
        }

        [RubyMethod("begin")]
        public static object Begin(MatchData/*!*/ self, [DefaultProtocol]int groupIndex) {
            return Bound(self.GetGroupOffsets(groupIndex, false), 0);
        }

        [RubyMethod("begin")]
        public static object Begin(MatchData/*!*/ self, [NotNull]RubySymbol/*!*/ groupName) {
            return Bound(self.GetGroupOffsets(groupName.ToString(), false), 0);
        }

        [RubyMethod("begin")]
        public static object Begin(MatchData/*!*/ self, [NotNull]MutableString/*!*/ groupName) {
            return Bound(self.GetGroupOffsets(groupName.ToString(), false), 0);
        }

        [RubyMethod("end")]
        public static object End(MatchData/*!*/ self, [DefaultProtocol]int groupIndex) {
            return Bound(self.GetGroupOffsets(groupIndex, false), 1);
        }

        [RubyMethod("end")]
        public static object End(MatchData/*!*/ self, [NotNull]RubySymbol/*!*/ groupName) {
            return Bound(self.GetGroupOffsets(groupName.ToString(), false), 1);
        }

        [RubyMethod("end")]
        public static object End(MatchData/*!*/ self, [NotNull]MutableString/*!*/ groupName) {
            return Bound(self.GetGroupOffsets(groupName.ToString(), false), 1);
        }

        [RubyMethod("offset")]
        public static RubyArray/*!*/ Offset(MatchData/*!*/ self, [DefaultProtocol]int groupIndex) {
            return BoundPair(self.GetGroupOffsets(groupIndex, false));
        }

        [RubyMethod("offset")]
        public static RubyArray/*!*/ Offset(MatchData/*!*/ self, [NotNull]RubySymbol/*!*/ groupName) {
            return BoundPair(self.GetGroupOffsets(groupName.ToString(), false));
        }

        [RubyMethod("offset")]
        public static RubyArray/*!*/ Offset(MatchData/*!*/ self, [NotNull]MutableString/*!*/ groupName) {
            return BoundPair(self.GetGroupOffsets(groupName.ToString(), false));
        }

        [RubyMethod("bytebegin")]
        public static object ByteBegin(MatchData/*!*/ self, [DefaultProtocol]int groupIndex) {
            return Bound(self.GetGroupOffsets(groupIndex, true), 0);
        }

        [RubyMethod("bytebegin")]
        public static object ByteBegin(MatchData/*!*/ self, [NotNull]RubySymbol/*!*/ groupName) {
            return Bound(self.GetGroupOffsets(groupName.ToString(), true), 0);
        }

        [RubyMethod("bytebegin")]
        public static object ByteBegin(MatchData/*!*/ self, [NotNull]MutableString/*!*/ groupName) {
            return Bound(self.GetGroupOffsets(groupName.ToString(), true), 0);
        }

        [RubyMethod("byteend")]
        public static object ByteEnd(MatchData/*!*/ self, [DefaultProtocol]int groupIndex) {
            return Bound(self.GetGroupOffsets(groupIndex, true), 1);
        }

        [RubyMethod("byteend")]
        public static object ByteEnd(MatchData/*!*/ self, [NotNull]RubySymbol/*!*/ groupName) {
            return Bound(self.GetGroupOffsets(groupName.ToString(), true), 1);
        }

        [RubyMethod("byteend")]
        public static object ByteEnd(MatchData/*!*/ self, [NotNull]MutableString/*!*/ groupName) {
            return Bound(self.GetGroupOffsets(groupName.ToString(), true), 1);
        }

        [RubyMethod("byteoffset")]
        public static RubyArray/*!*/ ByteOffset(MatchData/*!*/ self, [DefaultProtocol]int groupIndex) {
            return BoundPair(self.GetGroupOffsets(groupIndex, true));
        }

        [RubyMethod("byteoffset")]
        public static RubyArray/*!*/ ByteOffset(MatchData/*!*/ self, [NotNull]RubySymbol/*!*/ groupName) {
            return BoundPair(self.GetGroupOffsets(groupName.ToString(), true));
        }

        [RubyMethod("byteoffset")]
        public static RubyArray/*!*/ ByteOffset(MatchData/*!*/ self, [NotNull]MutableString/*!*/ groupName) {
            return BoundPair(self.GetGroupOffsets(groupName.ToString(), true));
        }

        #endregion

        [RubyMethod("length")]
        [RubyMethod("size")]
        public static int Length(MatchData/*!*/ self) {
            return self.GroupCount;
        }

        /// <summary>The pattern that produced the match - the very object, not a copy of it.</summary>
        [RubyMethod("regexp")]
        public static RubyRegex Regexp(MatchData/*!*/ self) {
            return self.Regexp;
        }

        [RubyMethod("==")]
        [RubyMethod("eql?")]
        public static bool Equals(RubyContext/*!*/ context, MatchData/*!*/ self, object other) {
            MatchData data = other as MatchData;
            if (data == null) {
                return false;
            }
            if (ReferenceEquals(self, data)) {
                return true;
            }
            if (!self.OriginalString.Equals(data.OriginalString)) {
                return false;
            }
            if (self.Regexp == null || data.Regexp == null) {
                return self.Regexp == data.Regexp;
            }
            return self.Regexp.Equals(data.Regexp)
                && self.Index == data.Index
                && self.Length == data.Length;
        }

        [RubyMethod("pre_match")]
        public static MutableString/*!*/ PreMatch(MatchData/*!*/ self) {
            return self.GetPreMatch();
        }

        [RubyMethod("post_match")]
        public static MutableString/*!*/ PostMatch(MatchData/*!*/ self) {
            return self.GetPostMatch();
        }

        private static RubyArray/*!*/ ReturnMatchingGroups(MatchData/*!*/ self, int groupIndex) {
            Debug.Assert(groupIndex >= 0);
            
            if (self.GroupCount < groupIndex) {
                return new RubyArray();
            }

            RubyArray result = new RubyArray(self.GroupCount - groupIndex);
            for (int i = groupIndex; i < self.GroupCount; i++) {
                result.Add(self.GetGroupValue(i));
            }
            return result;
        }

        [RubyMethod("captures")]
        public static RubyArray/*!*/ Captures(MatchData/*!*/ self) {
            return ReturnMatchingGroups(self, 1);
        }

        [RubyMethod("to_a")]
        public static RubyArray/*!*/ ToArray(MatchData/*!*/ self) {
            return ReturnMatchingGroups(self, 0);
        }

        // The same frozen String every time: md.string.equal?(md.string).
        [RubyMethod("string")]
        public static MutableString/*!*/ ReturnFrozenString(MatchData/*!*/ self) {
            return self.FrozenOriginalString;
        }

        [RubyMethod("values_at")]
        public static RubyArray/*!*/ ValuesAt(ConversionStorage<int>/*!*/ conversionStorage, 
            MatchData/*!*/ self, [DefaultProtocol]params int[]/*!*/ indices) {

            RubyArray result = new RubyArray();
            for (int i = 0; i < indices.Length; i++) {
                result.Add(GetGroup(self, indices[i]));
            }
            return result;
        }

        /// <summary>
        /// #&lt;MatchData "HX1138" 1:"H" 2:"X" 3:"113" 4:"8"&gt;, with the group's name in place of
        /// its number where it has one.
        /// </summary>
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, MatchData/*!*/ self) {
            MutableString result = MutableString.CreateMutable(self.Encoding);
            result.Append("#<MatchData ");
            result.Append(context.Inspect(self.GetGroupValue(0)));

            // Ruby does not number a group once the pattern names any of them - "(?<a>.)(.)" has
            // one capture, not two - and the transformation leaves such a group uncaptured, so
            // every group listed here is either numbered or named, each shared name once per group.
            for (int i = 1; i < self.GroupCount; i++) {
                result.Append(' ');
                result.Append(self.GetGroupName(i));
                result.Append(':');
                result.Append(context.Inspect(self.GetGroupValue(i)));
            }
            result.Append('>');
            return result;
        }

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(MatchData/*!*/ self) {
            return self.GetValue();
        }

        #endregion
    }
}

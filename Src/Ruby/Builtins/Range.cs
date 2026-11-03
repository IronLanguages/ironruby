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
using System.Runtime.Serialization;
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;
using Microsoft.Scripting.Generation;
using System.Runtime.CompilerServices;
using IronRuby.Runtime.Calls;
using System.Text;

namespace IronRuby.Builtins {

    public partial class Range : IDuplicable, ISerializable, IRubyObjectState {
        private const int FrozenFlag = 1;
        private const int TaintedFlag = 2;
        private const int UntrustedFlag = 4;

        private int _flags;
        private object _begin;
        private object _end;
        private bool _excludeEnd;
        private bool _initialized;

        public object Begin { get { return _begin; } }
        public object End { get { return _end; } }
        public bool ExcludeEnd { get { return _excludeEnd; } }

        protected Range(SerializationInfo info, StreamingContext context) {
            // A record Marshal read may lack any of the three (MRI's range_loader takes what
            // is there): a missing end is an endless range, a missing excl is inclusive.
            foreach (SerializationEntry entry in info) {
                switch (entry.Name) {
                    case "begin": _begin = entry.Value; break;
                    case "end": _end = entry.Value; break;
                    case "excl": _excludeEnd = entry.Value is bool && (bool)entry.Value; break;
                }
            }
            _initialized = true;
            // every Range is frozen, a loaded one included
            _flags |= FrozenFlag;
        }

        /// <summary>
        /// Range#initialize_copy: only an instance that was never initialized (Range.allocate)
        /// can take another range's data; every other Range is frozen.
        /// </summary>
        public void InitializeCopy(RubyContext/*!*/ context, Range/*!*/ other) {
            if (_initialized) {
                throw RubyExceptions.CreateObjectFrozenError(context, this);
            }
            _begin = other._begin;
            _end = other._end;
            _excludeEnd = other._excludeEnd;
            _initialized = true;
            // a copy is not frozen: (1..3).dup.frozen? is false in MRI
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context) {
            info.AddValue("begin", _begin);
            info.AddValue("end", _end);
            info.AddValue("excl", _excludeEnd);
        }

        protected Range(Range/*!*/ range) {
            _begin = range._begin;
            _end = range._end;
            _excludeEnd = range._excludeEnd;
        }

        public Range() {
        }

        public Range(int begin, int end, bool excludeEnd) {
            _begin = begin;
            _end = end;
            _excludeEnd = excludeEnd;
            _initialized = true;
            _flags |= FrozenFlag;
        }

        public Range(MutableString/*!*/ begin, MutableString/*!*/ end, bool excludeEnd) {
            _begin = begin;
            _end = end;
            _excludeEnd = excludeEnd;
            _initialized = true;
            _flags |= FrozenFlag;
        }
        
        // Convience function for constructing from C#, calls initialize
        public Range(BinaryOpStorage/*!*/ comparisonStorage, 
            RubyContext/*!*/ context, object begin, object end, bool excludeEnd) {
            Initialize(comparisonStorage, context, begin, end, excludeEnd);
        }

        public void Initialize(BinaryOpStorage/*!*/ comparisonStorage,
            RubyContext/*!*/ context, object begin, object end, bool excludeEnd) {

            if (_initialized) {
                // A range is frozen the moment it is initialized, so a second call is
                // a modification of a frozen object rather than a naming problem.
                throw RubyExceptions.CreateObjectFrozenError(context, this);
            }

            // Range tests whether the items can be compared, and uses that to determine if the range is valid
            // Only a non-existent <=> method or a result of nil seems to trigger the exception.
            // A nil end (endless range, 2.6) or nil begin (beginless range, 2.7) skips the test.
            if (begin != null && end != null) {
                object compareResult;

                // An exception raised by #<=> belongs to the caller: MRI only turns a
                // nil answer into "bad value for range".
                var site = comparisonStorage.GetCallSite("<=>");
                compareResult = site.Target(site, begin, end);

                if (compareResult == null) {
                    throw RubyExceptions.CreateArgumentError("bad value for range");
                }
            }

            _begin = begin;
            _end = end;
            _excludeEnd = excludeEnd;
            _initialized = true;
            // Ruby 3.0 froze every Range on creation.
            _flags |= FrozenFlag;
        }

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

        protected virtual Range/*!*/ Copy() {
            return new Range(this);
        }

        // Range doesn't have "initialize_copy", it's entirely initialized in dup:
        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = Copy();
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        private string/*!*/ Separator {
            get { return _excludeEnd ? "..." : ".."; }
        }

        public override string/*!*/ ToString() {
            var result = new StringBuilder();
            result.Append(_begin.ToString());
            result.Append(Separator);
            result.Append(_end.ToString());
            return result.ToString();
        }

        // MRI's inspect_range: an endless range drops its end and a beginless one drops
        // its begin, but (nil..nil) prints both so that it is not just "..".
        public MutableString/*!*/ Inspect(RubyContext/*!*/ context) {
            // The result takes the encoding of the first endpoint's inspect, as MRI's does:
            // US-ASCII for (1..3), UTF-8 for ("a".."c").
            MutableString first = (_begin != null || _end == null) ? context.Inspect(_begin) : null;
            MutableString last = (_begin == null || _end != null) ? context.Inspect(_end) : null;
            var result = MutableString.CreateMutable((first ?? last).Encoding);
            if (first != null) {
                result.Append(first);
            }
            result.Append(Separator);
            if (last != null) {
                result.Append(last);
            }
            return result;
        }

        public MutableString/*!*/ ToMutableString(ConversionStorage<MutableString>/*!*/ tosConversion) {
            // String#to_s answers the string itself, so appending to what the conversion
            // returns rewrites the range's own endpoint: ("ab".."zz").to_s used to leave
            // the begin string reading "ab..zz".
            MutableString begin = Protocols.ConvertToString(tosConversion, _begin);
            MutableString end = Protocols.ConvertToString(tosConversion, _end);

            var result = MutableString.CreateMutable(begin.Encoding);
            result.Append(begin);
            result.Append(Separator);
            result.Append(end);
            return result;
        }
    }
}

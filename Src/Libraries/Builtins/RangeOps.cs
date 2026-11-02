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
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Reflection;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Utils;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Builtins {
    using UnaryOp = Func<CallSite, object, object>;
    using BinaryOp = Func<CallSite, object, object, object>;
    using BinaryOpSite = CallSite<Func<CallSite, object, object, object>>;

    /// <summary>
    /// A Range represents an interval�a set of values with a start and an end.
    /// Ranges may be constructed using the s..e and s�e literals, or with Range::new.
    /// Ranges constructed using .. run from the start to the end inclusively.
    /// Those created using � exclude the end value.
    /// When used as an iterator, ranges return each value in the sequence. 
    /// </summary>
    /// <example>
    ///    (-1..-5).to_a      #=> []
    ///    (-5..-1).to_a      #=> [-5, -4, -3, -2, -1]
    ///    ('a'..'e').to_a    #=> ["a", "b", "c", "d", "e"]
    ///    ('a'...'e').to_a   #=> ["a", "b", "c", "d"]
    /// </example>
    /// <remarks>
    /// Ranges can be constructed using objects of any type, as long as the objects can be compared using their &lt;=&gt; operator
    /// and they support the succ method to return the next object in sequence. 
    /// </remarks>
    [RubyClass("Range", Extends = typeof(Range), Inherits = typeof(Object)), Includes(typeof(Enumerable))]
    public static class RangeOps {

        #region Construction and Initialization

        /// <summary>
        /// Construct a new Range object.
        /// </summary>
        /// <returns>
        /// An empty Range object
        /// </returns>
        /// <remarks>
        /// This constructor only creates an empty range object,
        /// which will be initialized subsequently by a separate call through into one of the two initialize methods.
        /// Literal Ranges (e.g. 1..5, 'a'...'b' are created by calls through to RubyOps.CreateInclusiveRange and
        /// RubyOps.CreateExclusiveRange which bypass this constructor/initializer run about and initialize the object directly.
        /// </remarks>
        [RubyConstructor]
        public static Range/*!*/ CreateRange(BinaryOpStorage/*!*/ comparisonStorage, 
            RubyClass/*!*/ self, object begin, object end, [Optional]bool excludeEnd) {
            return new Range(comparisonStorage, self.Context, begin, end, excludeEnd);
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static object Reinitialize(BinaryOpStorage/*!*/ comparisonStorage, 
            RubyContext/*!*/ context, Range/*!*/ self, object begin, object end, [Optional]bool excludeEnd) {
            self.Initialize(comparisonStorage, context, begin, end, excludeEnd);
            return null;
        }

        // Range.allocate.send(:initialize_copy, other) fills in an uninitialized range; on any
        // other it is the FrozenError every Range mutation is.
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static Range/*!*/ InitializeCopy(RubyContext/*!*/ context, Range/*!*/ self, object other) {
            var range = other as Range;
            if (range == null) {
                throw RubyExceptions.CreateTypeError("initialize_copy should take same class object");
            }
            self.InitializeCopy(context, range);
            return self;
        }

        #endregion

        #region begin, first, end, last, exclude_end?

        /// <summary>
        /// Returns the first object in self
        /// </summary>
        [RubyMethod("begin"), RubyMethod("first")]
        public static object Begin([NotNull]Range/*!*/ self) {
            return self.Begin;
        }

        /// <summary>
        /// Returns the object that defines the end of self
        /// </summary>
        [RubyMethod("end"), RubyMethod("last")]
        public static object End([NotNull]Range/*!*/ self) {
            return self.End;
        }

        /// <summary>
        /// Returns true if self excludes its end value. 
        /// </summary>
        [RubyMethod("exclude_end?")]
        public static bool ExcludeEnd([NotNull]Range/*!*/ self) {
            return self.ExcludeEnd;
        }

        #endregion

        #region inspect, to_s

        /// <summary>
        /// Convert this range object to a printable form (using inspect to convert the start and end objects). 
        /// </summary>
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, Range/*!*/ self) {
            return self.Inspect(context);
        }

        /// <summary>
        /// Convert this range object to a printable form (using to_s to convert the start and end objects).
        /// </summary>
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(ConversionStorage<MutableString>/*!*/ tosConversion, Range/*!*/ self) {
            return self.ToMutableString(tosConversion);
        }

        #endregion

        #region ==, eql?

        /// <summary>
        /// Is other equal to self?  Here other is not a Range so returns false.
        /// </summary>
        [RubyMethod("=="), RubyMethod("eql?")]
        public static bool Equals(Range/*!*/ self, object other) {
            return false;
        }

        /// <summary>
        /// Returns true only if self is a Range, has equivalent beginning and end items (by comparing them with ==),
        /// and has the same exclude_end? setting as <i>other</i>. 
        /// </summary>
        /// <example>
        /// (0..2) == (0..2)            #=> true
        /// (0..2) == Range.new(0,2)    #=> true
        /// (0..2) == (0...2)           #=> false
        /// (0..2).eql?(0.0..2.0)       #=> true
        /// </example>
        [RubyMethod("==")]
        public static bool Equals(BinaryOpStorage/*!*/ equals, Range/*!*/ self, [NotNull]Range/*!*/ other) {
            if (self == other) {
                return true;
            }
            return Protocols.IsEqual(equals, self.Begin, other.Begin)
                && Protocols.IsEqual(equals, self.End, other.End)
                && self.ExcludeEnd == other.ExcludeEnd;
        }

        /// <summary>
        /// Returns true only if self is a Range, has equivalent beginning and end items (by comparing them with eql?),
        /// and has the same exclude_end? setting as <i>other</i>. 
        /// </summary>
        /// <example>
        /// (0..2).eql?(0..2)             #=> true
        /// (0..2).eql?(Range.new(0,2))   #=> true
        /// (0..2).eql?(0...2)            #=> false
        /// (0..2).eql?(0.0..2.0)         #=> false
        /// </example>
        [RubyMethod("eql?")]
        public static bool Eql(BinaryOpStorage/*!*/ equalsStorage, Range/*!*/ self, [NotNull]Range/*!*/ other) {
            if (self == other) {
                return true;
            }

            var site = equalsStorage.GetCallSite("eql?");
            return Protocols.IsTrue(site.Target(site, self.Begin, other.Begin))
                && Protocols.IsTrue(site.Target(site, self.End, other.End))
                && self.ExcludeEnd == other.ExcludeEnd;
        }

        #endregion

        #region ===, member?, include?

        /// <summary>
        /// Returns true if other is an element of self, false otherwise.
        /// Conveniently, === is the comparison operator used by case statements. 
        /// </summary>
        /// <example>
        /// case 79
        ///   when 1..50   then   print "low\n"
        ///   when 51..75  then   print "medium\n"
        ///   when 76..100 then   print "high\n"
        /// end
        /// => "high"
        /// </example>
        [RubyMethod("==="), RubyMethod("member?"), RubyMethod("include?"), RubyMethod("cover?")]
        public static bool CaseEquals(ComparisonStorage/*!*/ comparisonStorage, [NotNull]Range/*!*/ self, object value) {
            var compare = comparisonStorage.CompareSite;

            // A nil endpoint is a beginless or endless range rather than a value to compare
            // against: (1..) covers everything from 1 up, (..10) everything up to 10. Comparing
            // nil <=> value yields nil, which used to make every such range answer false.
            if (self.Begin != null) {
                object result = compare.Target(compare, self.Begin, value);
                if (result == null || Protocols.ConvertCompareResult(comparisonStorage, result) > 0) {
                    return false;
                }
            }

            if (self.End == null) {
                return true;
            }

            object endResult = compare.Target(compare, value, self.End);
            if (endResult == null) {
                return false;
            }

            int valueToEnd = Protocols.ConvertCompareResult(comparisonStorage, endResult);
            return valueToEnd < 0 || (!self.ExcludeEnd && valueToEnd == 0);
        }

        /// <summary>
        /// Since 2.6 cover? also takes a Range, and answers whether self contains the whole of it.
        /// === / include? / member? keep the single-value meaning only.
        /// </summary>
        [RubyMethod("cover?")]
        public static bool Cover(ComparisonStorage/*!*/ comparisonStorage, UnaryOpStorage/*!*/ maxStorage,
            [NotNull]Range/*!*/ self, [NotNull]Range/*!*/ other) {
            var compare = comparisonStorage.CompareSite;

            // an unbounded end cannot be covered by a bounded one, and likewise for the beginning
            if (self.End != null && other.End == null) {
                return false;
            }
            if (self.Begin != null && other.Begin == null) {
                return false;
            }

            // an empty range is covered by nothing, not by everything
            if (other.Begin != null && other.End != null) {
                object span = compare.Target(compare, other.Begin, other.End);
                if (span != null) {
                    int beginToEnd = Protocols.ConvertCompareResult(comparisonStorage, span);
                    if (beginToEnd > (other.ExcludeEnd ? -1 : 0)) {
                        return false;
                    }
                }
            }

            if (other.Begin != null && !CaseEquals(comparisonStorage, self, other.Begin)) {
                return false;
            }

            int selfEndToOtherEnd;
            if (self.End == null) {
                // An unbounded end sits past any bounded one; two unbounded ends finish in
                // the same place, so only the exclusivity below can still separate them -
                // which is why (0...) does not cover (4..).
                selfEndToOtherEnd = (other.End == null) ? 0 : 1;
            } else {
                object result = compare.Target(compare, self.End, other.End);
                if (result == null) {
                    return false;
                }
                selfEndToOtherEnd = Protocols.ConvertCompareResult(comparisonStorage, result);
            }
            if (self.ExcludeEnd == other.ExcludeEnd) {
                return selfEndToOtherEnd >= 0;
            }
            if (self.ExcludeEnd) {
                return selfEndToOtherEnd > 0;
            }
            if (selfEndToOtherEnd >= 0) {
                return true;
            }

            // self is inclusive and other is not, so other's endpoint is one past its last
            // element: (1..10).cover?(1...11) holds. Ask other for its max rather than trying
            // to compute a predecessor - MRI does the same, and treats a max it cannot produce
            // (a Float range, say) as not covered.
            var maxSite = maxStorage.GetCallSite("max");
            object otherMax;
            try {
                otherMax = maxSite.Target(maxSite, other);
            } catch (InvalidOperationException) {
                return false;
            }

            if (otherMax == null) {
                return false;
            }

            object maxResult = compare.Target(compare, self.End, otherMax);
            if (maxResult == null) {
                return false;
            }
            return Protocols.ConvertCompareResult(comparisonStorage, maxResult) >= 0;
        }

        /// <summary>
        /// Ruby 3.3. True when the two ranges have at least one element in common.
        /// </summary>
        [RubyMethod("overlap?")]
        public static bool Overlap(ComparisonStorage/*!*/ comparisonStorage, [NotNull]Range/*!*/ self, object other) {
            var otherRange = other as Range;
            if (otherRange == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Range)",
                    comparisonStorage.Context.GetClassDisplayName(other));
            }
            return Overlap(comparisonStorage, self, otherRange);
        }

        private static bool Overlap(ComparisonStorage/*!*/ comparisonStorage, Range/*!*/ self, Range/*!*/ other) {
            var compare = comparisonStorage.CompareSite;

            if (IsEmpty(comparisonStorage, self) || IsEmpty(comparisonStorage, other)) {
                return false;
            }

            // they overlap unless one ends before the other begins
            if (!EndsBefore(comparisonStorage, self.End, self.ExcludeEnd, other.Begin) &&
                !EndsBefore(comparisonStorage, other.End, other.ExcludeEnd, self.Begin)) {
                return true;
            }
            return false;
        }

        private static bool IsEmpty(ComparisonStorage/*!*/ comparisonStorage, Range/*!*/ range) {
            if (range.Begin == null || range.End == null) {
                return false;
            }
            var compare = comparisonStorage.CompareSite;
            object result = compare.Target(compare, range.Begin, range.End);
            if (result == null) {
                return false;
            }
            int beginToEnd = Protocols.ConvertCompareResult(comparisonStorage, result);
            return beginToEnd > (range.ExcludeEnd ? -1 : 0);
        }

        // Does a range ending at end (exclusive when excludeEnd) finish before begin starts?
        // Endpoints that cannot be compared at all count as "before": MRI's
        // empty_region_p treats a nil answer from #<=> as the widest possible gap, so
        // (0..2).overlap?('a'..'d') is false rather than an exception.
        private static bool EndsBefore(ComparisonStorage/*!*/ comparisonStorage, object end, bool excludeEnd, object begin) {
            if (end == null || begin == null) {
                return false;
            }
            var compare = comparisonStorage.CompareSite;
            object result = compare.Target(compare, end, begin);
            if (result == null) {
                return true;
            }
            int endToBegin = Protocols.ConvertCompareResult(comparisonStorage, result);
            return endToBegin < 0 || (excludeEnd && endToBegin == 0);
        }

        #endregion

        #region hash

        /// <summary>
        /// Generate a hash value such that two ranges with the same start and end points,
        /// and the same value for the "exclude end" flag, generate the same hash value. 
        /// </summary>
        [RubyMethod("hash")]
        public static int GetHashCode(UnaryOpStorage/*!*/ hashStorage, Range/*!*/ self) {
            // MRI: Ruby treatment of hash return value is inconsistent. 
            // No conversions happen here (unlike e.g. Array.hash).
            var hashSite = hashStorage.GetCallSite("hash");
            return unchecked(
                Protocols.ToHashCode(hashSite.Target(hashSite, self.Begin)) ^
                Protocols.ToHashCode(hashSite.Target(hashSite, self.End)) ^ 
                (self.ExcludeEnd ? 179425693 : 1794210891)
            );
        }

        #endregion

        #region each, step

        public class EachStorage : ComparisonStorage {
            private CallSite<Func<CallSite, object, MutableString>> _stringCast;
            private CallSite<BinaryOp> _equalsSite;    // ==
            private CallSite<UnaryOp> _succSite;       // succ
            private CallSite<BinaryOp> _respondToSite; // respond_to?

            [Emitted]
            public EachStorage(RubyContext/*!*/ context) : base(context) { }

            public CallSite<Func<CallSite, object, MutableString>>/*!*/ StringCastSite {
                get { return RubyUtils.GetCallSite(ref _stringCast, ConvertToStrAction.Make(Context)); }
            }

            public CallSite<BinaryOp>/*!*/ RespondToSite {
                get { return RubyUtils.GetCallSite(ref _respondToSite, Context, "respond_to?", 1); }
            }

            public CallSite<BinaryOp>/*!*/ EqualsSite {
                get { return RubyUtils.GetCallSite(ref _equalsSite, Context, "==", 1); }
            }

            public CallSite<UnaryOp>/*!*/ SuccSite {
                get { return RubyUtils.GetCallSite(ref _succSite, Context, "succ", 0); }
            }
        }

        public class StepStorage : EachStorage {
            private CallSite<Func<CallSite, object, int>> _fixnumCast;
            private CallSite<BinaryOp> _lessThanEqualsSite; // <=
            private CallSite<BinaryOp> _addSite;            // +

            [Emitted]
            public StepStorage(RubyContext/*!*/ context) : base(context) { }

            public CallSite<Func<CallSite, object, int>>/*!*/ FixnumCastSite {
                get { return RubyUtils.GetCallSite(ref _fixnumCast, ConvertToFixnumAction.Make(Context)); }
            }

            public CallSite<BinaryOp>/*!*/ LessThanEqualsSite {
                get { return RubyUtils.GetCallSite(ref _lessThanEqualsSite, Context, "<=", 1); }
            }

            public CallSite<BinaryOp>/*!*/ AddSite {
                get { return RubyUtils.GetCallSite(ref _addSite, Context, "+", 1); }
            }
        }

        [RubyMethod("each")]
        public static Enumerator/*!*/ GetEachEnumerator(EachStorage/*!*/ storage, Range/*!*/ self) {
            return new Enumerator(self, "each");
        }

        [RubyMethod("bsearch")]
        public static Enumerator/*!*/ GetBinarySearchEnumerator(Range/*!*/ self) {
            return new Enumerator(self, "bsearch");
        }

        /// <summary>
        /// Binary search over an Integer range. The block decides which way to go: true/false
        /// selects the find-minimum mode and returns the smallest element the block accepts,
        /// while an Integer selects find-any mode - 0 means this is the element, and the sign
        /// says which half to keep. A block returning anything else is a TypeError.
        /// </summary>
        [RubyMethod("bsearch")]
        public static object BinarySearch(ConversionStorage<int>/*!*/ fixnumCast, [NotNull]BlockParam/*!*/ block, Range/*!*/ self) {
            if (!(self.Begin is int || self.Begin == null) || !(self.End is int || self.End == null)) {
                throw RubyExceptions.CreateTypeError("can't do binary search for {0}",
                    block.RubyContext.GetClassName(self.Begin ?? self.End));
            }

            // long rather than int throughout: an endless range's upper bound starts at
            // Int32.MaxValue, and (high - low) for a negative beginning overflows int - which
            // produced a negative midpoint outside the range and a loop that never terminated.
            long low = self.Begin == null ? Int32.MinValue : (int)self.Begin;
            long high;
            if (self.End == null) {
                high = Int32.MaxValue;
            } else {
                high = (int)self.End;
                if (self.ExcludeEnd) {
                    high--;
                }
            }

            object satisfied = null;
            object found = null;

            // An unbounded end has no midpoint to bisect towards, so probe beg+1, beg+2, beg+4 …
            // until the block first accepts, and bisect the interval that leaves. This is what
            // MRI does, and it keeps the answer correct past Int32.MaxValue.
            if (self.End == null) {
                long step = 1;
                long probe = low;
                while (true) {
                    if (probe > Int32.MaxValue - step) {
                        high = Int32.MaxValue;
                        break;
                    }
                    probe = low + step;

                    bool probeLower;
                    if (!Probe(block, probe, ref satisfied, ref found, out probeLower)) {
                        return found;
                    }
                    if (probeLower) {
                        high = probe;
                        break;
                    }
                    low = probe;
                    step *= 2;
                }
                low = self.Begin == null ? Int32.MinValue : (int)self.Begin;
            }

            while (low <= high) {
                long middle = low + ((high - low) >> 1);

                bool searchLower;
                if (!Probe(block, middle, ref satisfied, ref found, out searchLower)) {
                    return found;
                }

                if (searchLower) {
                    high = middle - 1;
                } else {
                    low = middle + 1;
                }
            }

            return satisfied;
        }

        /// <summary>
        /// Calls the block with one candidate. Returns false when the search is over - either the
        /// block broke out or it answered 0 - leaving the answer in found.
        /// </summary>
        private static bool Probe(BlockParam/*!*/ block, long candidate, ref object satisfied, ref object found, out bool searchLower) {
            object element = Protocols.Normalize(candidate);

            object result;
            if (block.Yield(element, out result)) {
                found = result;
                searchLower = false;
                return false;
            }

            if (result is int) {
                int direction = (int)result;
                if (direction == 0) {
                    found = element;
                    searchLower = false;
                    return false;
                }
                searchLower = direction < 0;
            } else if (result is double) {
                double direction = (double)result;
                if (direction == 0.0) {
                    found = element;
                    searchLower = false;
                    return false;
                }
                searchLower = direction < 0.0;
            } else if (result is bool) {
                searchLower = (bool)result;
                if (searchLower) {
                    satisfied = element;
                }
            } else if (result == null) {
                searchLower = false;
            } else {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (must be numeric, true, false or nil)",
                    block.RubyContext.GetClassName(result));
            }
            return true;
        }

        /// <summary>
        /// Iterates over the elements of self, passing each in turn to the block.
        /// You can only iterate if the start object of the range supports the succ method
        /// (which means that you can�t iterate over ranges of Float objects). 
        /// </summary>
        [RubyMethod("each")]
        public static object Each(EachStorage/*!*/ storage, [NotNull]BlockParam/*!*/ block, Range/*!*/ self) {
            if (self.End == null) {
                return StepEndless(storage, block, self, self.Begin, 1);
            } else if (self.Begin is int && self.End is int) {
                return StepFixnum(block, self, (int)self.Begin, (int)self.End, 1);
            } else if (self.Begin is MutableString) {
                return StepString(storage, block, self, (MutableString)self.Begin, (MutableString)self.End, 1);
            } else if (self.Begin is RubySymbol && self.End is RubySymbol) {
                return StepSymbol(storage, block, self, (RubySymbol)self.Begin, (RubySymbol)self.End, 1);
            } else {
                return StepObject(storage, block, self, self.Begin, self.End, 1);
            }
        }

        /// <summary>
        /// MRI iterates a Symbol range through String#upto on the symbol names, so
        /// (:A..:z).to_a has 58 entries. Walking Symbol#succ instead never terminated: :Z.succ
        /// is :AA, which still sorts before :z, and so on forever.
        /// </summary>
        private static object StepSymbol(EachStorage/*!*/ storage, BlockParam/*!*/ block, Range/*!*/ self, RubySymbol/*!*/ begin, RubySymbol/*!*/ end, int step) {
            var context = storage.Context;
            return StepStringCore(storage, block, self, begin.String, end.String, step,
                (str) => context.CreateSymbol(str, false));
        }

        [RubyMethod("step")]
        public static Enumerator/*!*/ GetStepEnumerator(StepStorage/*!*/ storage, Range/*!*/ self, [Optional]object step) {
            return new Enumerator((_, block) => Step(storage, block, self, step));
        }

        /// <summary>
        /// Iterates over self, passing each stepth element to the block.
        /// If the range contains numbers or strings, natural ordering is used.
        /// Otherwise step invokes succ to iterate through range elements.
        /// </summary>
        [RubyMethod("step")]
        public static object Step(StepStorage/*!*/ storage, [NotNull]BlockParam/*!*/ block, Range/*!*/ self, [Optional]object step) {
            if (step == Missing.Value) {
                step = ClrInteger.One;
            }

            // We attempt to cast step to Fixnum here even though if we were iterating over Floats, for instance, we use step as is.
            // This prevents cases such as (1.0..2.0).step(0x800000000000000) {|x| x } from working but that is what MRI does.
            if (self.Begin is MutableString && step is double) {
                // MRI only steps a String range by an Integer; a Float step is a TypeError there,
                // and here it would silently truncate and then loop forever over an endless range.
                throw RubyExceptions.CreateTypeError("no implicit conversion to integer from float");
            }

            if (self.End == null && (self.Begin is int || self.Begin is MutableString)) {
                var endlessStep = storage.FixnumCastSite;
                return StepEndless(storage, block, self, self.Begin, endlessStep.Target(endlessStep, step));
            } else if (self.Begin is int && self.End is int) {
                // self.begin is Fixnum; directly call item = item + 1 instead of succ
                var site = storage.FixnumCastSite;
                int intStep = site.Target(site, step);
                return StepFixnum(block, self, (int)self.Begin, (int)self.End, intStep);
            } else if (self.Begin is MutableString) {
                // self.begin is String; use item.succ and item <=> self.end but make sure you check the length of the strings
                var site = storage.FixnumCastSite;
                int intStep = site.Target(site, step);
                return StepString(storage, block, self, (MutableString)self.Begin, (MutableString)self.End, intStep);
            } else if (storage.Context.IsInstanceOf(self.Begin, storage.Context.GetClass(typeof(Numeric)))) {
                // self.begin is Numeric; invoke item = item + 1 instead of succ and invoke < or <= for compare
                return StepNumeric(storage, block, self, self.Begin, self.End, step);
            } else {
	            // self.begin is not Numeric or String; just invoke item.succ and item <=> self.end
                var site = storage.FixnumCastSite;
                int intStep = site.Target(site, step);
                return StepObject(storage, block, self, self.Begin, self.End, intStep);
            }
        }

        /// <summary>
        /// Step through a Range of Fixnums.
        /// </summary>
        /// <remarks>
        /// This method is optimized for direct integer operations using &lt; and + directly.
        /// It is not used if either begin or end are outside Fixnum bounds.
        /// </remarks>
        private static object StepFixnum(BlockParam/*!*/ block, Range/*!*/ self, int begin, int end, int step) {
            Assert.NotNull(block, self);
            CheckStep(step);

            object result;
            int item = begin;
            while (item < end) {
                if (block.Yield(item, out result)) {
                    return result;
                }
                item += step;
            }

            if (item == end && !self.ExcludeEnd) {
                if (block.Yield(item, out result)) {
                    return result;
                }
            }
            return self;
        }

        /// <summary>
        /// Step through an endless range (1.., "a"..). It only terminates when the block breaks,
        /// which is what makes (1..).min(2) or (1..).lazy work.
        /// </summary>
        private static object StepEndless(EachStorage/*!*/ storage, BlockParam/*!*/ block, Range/*!*/ self, object begin, int step) {
            Assert.NotNull(storage, block, self);
            CheckStep(step);

            object result;
            if (begin is int) {
                // long, so that walking off the end of Fixnum promotes to Bignum the way MRI does
                long item = (int)begin;
                while (true) {
                    if (block.Yield(Protocols.Normalize(item), out result)) {
                        return result;
                    }
                    item += step;
                }
            }

            CheckBegin(storage, begin);
            object current = begin;
            var succSite = storage.SuccSite;
            while (true) {
                if (block.Yield(current is MutableString ? ((MutableString)current).Clone() : current, out result)) {
                    return result;
                }
                for (int i = 0; i < step; i++) {
                    current = succSite.Target(succSite, current);
                }
            }
        }

        /// <summary>
        /// Step through a Range of Strings.
        /// </summary>
        /// <remarks>
        /// This method requires step to be a Fixnum.
        /// It uses a hybrid string comparison to prevent infinite loops and calls String#succ to get each item in the range.
        /// </remarks>
        private static object StepString(EachStorage/*!*/ storage, BlockParam/*!*/ block, Range/*!*/ self, MutableString begin, MutableString end, int step) {
            return StepStringCore(storage, block, self, begin, end, step, null);
        }

        /// <summary>
        /// The body shared by String and Symbol ranges. <paramref name="wrap"/> turns each
        /// produced string into the value the block sees (null means "yield the string itself").
        /// </summary>
        private static object StepStringCore(EachStorage/*!*/ storage, BlockParam/*!*/ block, Range/*!*/ self,
            MutableString/*!*/ begin, MutableString/*!*/ end, int step, Func<MutableString, object> wrap) {
            Assert.NotNull(storage, block, self);
            CheckStep(step);
            object result;

            // MRI's String#upto walks one-byte endpoints by character code, which is why
            // ("A".."z") has 58 elements rather than the 26 that #succ would produce.
            if (begin.GetByteCount() == 1 && end.GetByteCount() == 1) {
                int from = begin.GetByte(0), to = end.GetByte(0);
                for (int c = from; self.ExcludeEnd ? c < to : c <= to; c += step) {
                    var item = MutableString.CreateBinary(new byte[] { (byte)c }, begin.Encoding);
                    if (block.Yield(wrap != null ? wrap(item) : item, out result)) {
                        return result;
                    }
                }
                return self;
            }

            if (Protocols.Compare(storage, begin, end) > 0) {
                return self;
            }

            // MRI's str_upto_each stops when the successor of the end turns up, not when
            // the current value sorts past the end: "a".upto("ab") has to pass through
            // "b".."z", every one of which sorts after "ab". Comparing against the end
            // instead stopped the walk at "b", so ("a".."ab").to_a held one element.
            MutableString current = begin.CloneDerived();
            MutableString afterEnd = MutableStringOps.Succ(end);

            int index = 0;
            while (Protocols.Compare(storage, current, afterEnd) != 0) {
                bool atEnd = Protocols.Compare(storage, current, end) == 0;
                if (atEnd && self.ExcludeEnd) {
                    return self;
                }

                if (index % step == 0) {
                    if (block.Yield(wrap != null ? wrap(current.Clone()) : current.Clone(), out result)) {
                        return result;
                    }
                }
                index++;

                if (atEnd) {
                    return self;
                }

                MutableStringOps.SuccInPlace(current);
                // lengths in bytes, as MRI (RSTRING_LEN) measures them
                if (current.GetByteCount() > end.GetByteCount() || current.IsEmpty) {
                    return self;
                }
            }
            return self;
        }

        /// <summary>
        /// Step through a Range of Numerics.
        /// </summary>
        private static object StepNumeric(StepStorage/*!*/ storage, BlockParam/*!*/ block, Range/*!*/ self, object begin, object end, object step) {
            Assert.NotNull(storage, block, self);
            CheckStep(storage, step);

            object item = begin;
            object result;

            var site = self.ExcludeEnd ? storage.LessThanSite : storage.LessThanEqualsSite;
            while (RubyOps.IsTrue(site.Target(site, item, end))) {
                if (block.Yield(item, out result)) {
                    return result;
                }

                var add = storage.AddSite;
                item = add.Target(add, item, step);
            }

            return self;
        }

        /// <summary>
        /// Step through a Range of objects that are not Numeric or String.
        /// </summary>
        private static object StepObject(EachStorage/*!*/ storage, BlockParam/*!*/ block, Range/*!*/ self, object begin, object end, int step) {
            Assert.NotNull(storage, block, self);
            CheckStep(storage, step);
            CheckBegin(storage, self.Begin);

            object item = begin, result;
            int comp;

            var succSite = storage.SuccSite;
            while ((comp = Protocols.Compare(storage, item, end)) < 0) {
                if (block.Yield(item, out result)) {
                    return result;
                }

                for (int i = 0; i < step; ++i) {
                    item = succSite.Target(succSite, item);
                }
            }

            if (comp == 0 && !self.ExcludeEnd) {
                if (block.Yield(item, out result)) {
                    return result;
                }
            }
            return self;
        }

        /// <summary>
        /// Check that the int is not less than or equal to zero.
        /// </summary>
        private static void CheckStep(int step) {
            if (step == 0) {
                throw RubyExceptions.CreateArgumentError("step can't be 0");
            }
            if (step < 0) {
                throw RubyExceptions.CreateArgumentError("step can't be negative");
            }
        }

        /// <summary>
        /// Check that the object, when converted to an integer, is not less than or equal to zero.
        /// </summary>
        private static void CheckStep(EachStorage/*!*/ storage, object step) {
            var equals = storage.EqualsSite;
            if (Protocols.IsTrue(equals.Target(equals, step, 0))) {
                throw RubyExceptions.CreateArgumentError("step can't be 0");
            }

            var lessThan = storage.LessThanSite;
            if (RubyOps.IsTrue(lessThan.Target(lessThan, step, 0))) {
                throw RubyExceptions.CreateArgumentError("step can't be negative");
            }
        }

        /// <summary>
        /// Check that the object responds to "succ".
        /// </summary>
        private static void CheckBegin(EachStorage/*!*/ storage, object begin) {
            if (!Protocols.RespondTo(storage.RespondToSite, storage.Context, begin, "succ")) {
                throw RubyExceptions.CreateTypeError("can't iterate from {0}", storage.Context.GetClassDisplayName(begin));
            }
        }

        #endregion
    }
}

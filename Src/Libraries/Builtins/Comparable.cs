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
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    [RubyModule("Comparable")]
    public static class Comparable {
        [RubyMethod("<")]
        public static bool Less(ComparisonStorage/*!*/ comparisonStorage, object self, object other) {
            return Compare(comparisonStorage, self, other).GetValueOrDefault(0) < 0;
        }

        [RubyMethod("<=")]
        public static bool LessOrEqual(ComparisonStorage/*!*/ comparisonStorage, object self, object other) {
            return Compare(comparisonStorage, self, other).GetValueOrDefault(1) <= 0;
        }

        [RubyMethod(">=")]
        public static bool GreaterOrEqual(ComparisonStorage/*!*/ comparisonStorage, object self, object other) {
            return Compare(comparisonStorage, self, other).GetValueOrDefault(-1) >= 0;
        }

        [RubyMethod(">")]
        public static bool Greater(ComparisonStorage/*!*/ comparisonStorage, object self, object other) {
            return Compare(comparisonStorage, self, other).GetValueOrDefault(0) > 0;
        }

        /// <summary>
        /// Try to compare the lhs and rhs. Throws and exception if comparison returns null. Returns null on failure, -1/0/+1 otherwise.
        /// </summary>
        private static int? Compare(ComparisonStorage/*!*/ comparisonStorage, object lhs, object rhs) {

            // calls method_missing, doesn't catch any exception:
            var compare = comparisonStorage.CompareSite;
            object compareResult = compare.Target(compare, lhs, rhs);
            if (compareResult != null) {
                return Protocols.ConvertCompareResult(comparisonStorage, compareResult);
            } 

            throw RubyExceptions.MakeComparisonError(comparisonStorage.Context, lhs, rhs);
        }

        #region clamp

        // MRI's cmp_clamp. It was Ruby in ruby4.rb, and a hot `x.clamp(0, 15)` paid for a method
        // scope, a splat array and a case on its size on every call.

        // MRI declares clamp with rb_scan_args "11", which makes its arity -1, not -2. This
        // overload is what gives the group that arity; it only takes the argument counts the
        // others do not, and raises MRI's error for them.
        [RubyMethod("clamp")]
        public static object Clamp(object self, params object[]/*!*/ args) {
            throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..2)", args.Length);
        }

        [RubyMethod("clamp")]
        public static object Clamp(ComparisonStorage/*!*/ comparisonStorage, object self, object min, object max) {
            if (min != null && max != null && CompareOrRaise(comparisonStorage, min, max) > 0) {
                throw RubyExceptions.CreateArgumentError("min argument must be less than or equal to max argument");
            }

            if (min != null) {
                int c = CompareOrRaise(comparisonStorage, self, min);
                if (c == 0) {
                    return self;
                }
                if (c < 0) {
                    return min;
                }
            }

            if (max != null && CompareOrRaise(comparisonStorage, self, max) > 0) {
                return max;
            }
            return self;
        }

        [RubyMethod("clamp")]
        public static object Clamp(ComparisonStorage/*!*/ comparisonStorage, object self, [NotNull]Range/*!*/ range) {
            return ClampToRange(comparisonStorage, self, range.Begin, range.End, range.ExcludeEnd);
        }

        // rb_range_values: anything with #begin, #end and #exclude_end? passes for a Range.
        [RubyMethod("clamp")]
        public static object Clamp(ComparisonStorage/*!*/ comparisonStorage, RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ beginStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ endStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ excludeEndStorage,
            object self, object range) {

            if (range == null || range is bool ||
                !Protocols.RespondTo(respondToStorage, range, "begin") ||
                !Protocols.RespondTo(respondToStorage, range, "end") ||
                !Protocols.RespondTo(respondToStorage, range, "exclude_end?")) {

                string name = range == null ? "nil" : range is bool ? ((bool)range ? "true" : "false") :
                    comparisonStorage.Context.GetClassOf(range).GetNonNullName(comparisonStorage.Context);
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Range)", name);
            }

            var beginSite = beginStorage.GetCallSite("begin", 0);
            var endSite = endStorage.GetCallSite("end", 0);
            var excludeEndSite = excludeEndStorage.GetCallSite("exclude_end?", 0);
            object min = beginSite.Target(beginSite, range);
            object max = endSite.Target(endSite, range);
            return ClampToRange(comparisonStorage, self, min, max, RubyOps.IsTrue(excludeEndSite.Target(excludeEndSite, range)));
        }

        private static object ClampToRange(ComparisonStorage/*!*/ comparisonStorage, object self, object min, object max, bool excludeEnd) {
            if (excludeEnd && max != null) {
                throw RubyExceptions.CreateArgumentError("cannot clamp with an exclusive range");
            }
            return Clamp(comparisonStorage, self, min, max);
        }

        /// <summary>
        /// MRI's cmpint: a nil result is a comparison error naming both operands.
        /// </summary>
        private static int CompareOrRaise(ComparisonStorage/*!*/ comparisonStorage, object lhs, object rhs) {
            var compare = comparisonStorage.CompareSite;
            object compareResult = compare.Target(compare, lhs, rhs);
            if (compareResult == null) {
                throw RubyExceptions.MakeComparisonError(comparisonStorage.Context, lhs, rhs);
            }
            return Protocols.ConvertCompareResult(comparisonStorage, compareResult);
        }

        #endregion

        [RubyMethod("between?")]
        public static bool Between(ComparisonStorage/*!*/ comparisonStorage, object self, object min, object max) {
            return !Less(comparisonStorage, self, min) && !Greater(comparisonStorage, self, max);
        }

        [RubyMethod("==")]
        public static bool Equal(ComparisonStorage/*!*/ comparisonStorage, object self, object other) {
            if (self == other) {
                return true;
            }

            // Ruby 2.3 stopped rescuing StandardError here: whatever <=> raises goes through,
            // a nil result is false and a non-numeric result raises (via the comparison below).
            var compare = comparisonStorage.CompareSite;
            object compareResult = compare.Target(compare, self, other);
            if (compareResult == null) {
                return false;
            }

            return Protocols.ConvertCompareResult(comparisonStorage, compareResult) == 0;
        }
    }
}

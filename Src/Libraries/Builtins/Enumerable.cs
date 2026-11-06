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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Osr;

namespace IronRuby.Builtins {
    using EachSite = Func<CallSite, object, Proc, object>;
    using EachSiteN = Func<CallSite, object, Proc, IList, object>;

    [RubyModule("Enumerable")]
    public static class Enumerable {
        #region Array fast paths for the prelude's methods

        /// <summary>
        /// True for an Array whose #each is the built-in Array#each, which yields each element
        /// once, in order, re-reading the length as it goes. The prelude's Enumerable methods then
        /// get the same answer from a loop over the list as from calling #each with a
        /// |*values| block, so they hand the work to the C# methods below.
        /// </summary>
        [RubyMethod("__plain_list__?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsPlainList(RubyContext/*!*/ context, object self) {
            if (!(self is RubyArray)) {
                return false;
            }
            RubyClass arrayClass = context.GetClass(typeof(RubyArray));
            if (!IListOps.IsBuiltinMethod(arrayClass, "each")) {
                return false;
            }
            RubyClass cls = context.GetImmediateClassOf(self);
            if (ReferenceEquals(cls, arrayClass)) {
                return true;
            }
            var own = cls.ResolveMethod("each", VisibilityContext.AllVisible);
            var builtin = arrayClass.ResolveMethod("each", VisibilityContext.AllVisible);
            return own.Found && builtin.Found && ReferenceEquals(own.Info, builtin.Info);
        }

        // Enumerable#group_by over a plain list (see IsPlainList): the prelude's loop with the
        // result Hash filled in directly, as MRI's group_by_i does.
        [RubyMethod("__list_group_by__", RubyMethodAttributes.PrivateInstance)]
        public static object ListGroupBy(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, [NotNull]IList/*!*/ self) {
            var result = new Hash(context);
            for (int i = 0; i < self.Count; i++) {
                object item = self[i];
                object key;
                if (block.Yield(item, out key)) {
                    return key;
                }
                object group;
                if (result.TryGetValue(CustomStringDictionary.NullToObj(key), out group)) {
                    ((RubyArray)group).Add(item);
                } else {
                    RubyUtils.SetHashElement(context, result, key, new RubyArray { item });
                }
            }
            return result;
        }

        // Enumerable#tally (with no Hash to add to) over a plain list: the counts are kept in the
        // result Hash as MRI's tally_up keeps them, without a dynamic Integer#+ per element.
        [RubyMethod("__list_tally__", RubyMethodAttributes.PrivateInstance)]
        public static Hash/*!*/ ListTally(RubyContext/*!*/ context, [NotNull]IList/*!*/ self) {
            var result = new Hash(context);
            for (int i = 0; i < self.Count; i++) {
                object item = self[i];
                object storedKey = CustomStringDictionary.NullToObj(item);
                object count;
                if (result.TryGetValue(storedKey, out count)) {
                    // a count cannot outgrow the list, so it stays a Fixnum
                    result[storedKey] = ScriptingRuntimeHelpers.Int32ToObject((int)count + 1);
                } else {
                    // MRI's tally_up stores the element itself: a String key is not copied and
                    // frozen the way Hash#[]= would
                    result[storedKey] = ScriptingRuntimeHelpers.Int32ToObject(1);
                }
            }
            return result;
        }

        // The prelude's #tally stores a count with this rather than with Hash#[]=, which would
        // store a copy of a String key, frozen: MRI's tally_up stores the element itself.
        [RubyMethod("__tally_store__", RubyMethodAttributes.PrivateInstance)]
        public static object TallyStore(object self, [NotNull]Hash/*!*/ hash, object key, object count) {
            hash.RequireNotFrozen();
            hash[CustomStringDictionary.NullToObj(key)] = count;
            return count;
        }

        #endregion

        internal static object Each(CallSiteStorage<EachSite>/*!*/ each, object self, Proc/*!*/ block) {
            var site = each.GetCallSite("each", new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock));
            return site.Target(site, self, block);
        }

        internal static object Each(CallSiteStorage<EachSiteN>/*!*/ each, object self, IList/*!*/ args, Proc/*!*/ block) {
            var site = each.GetCallSite("each", new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock | RubyCallFlags.HasSplattedArgument));
            return site.Target(site, self, block, args);
        }

        /// <summary>
        /// Packs the values a single `yield` handed the iteration block into the one object MRI
        /// stores for it: no value becomes nil, one value stays itself, several become an Array.
        /// `def each; yield 1, 2; end` is therefore what makes `to_a` produce [[1, 2]], not [1].
        /// </summary>
        internal static object PackValues(RubyArray/*!*/ args) {
            switch (args.Count) {
                case 0: return null;
                case 1: return args[0];
                default: return args;
            }
        }

        /// <summary>
        /// Creates the block Enumerable iterates with. It is declared `|*args|`, so it sees every
        /// value a yield passed rather than only the first, packs them per <see cref="PackValues"/>
        /// and hands the single item on. Re-yielding that item to a user block restores the
        /// original arguments, because Ruby auto-splats an Array into a multi-parameter block.
        /// </summary>
        internal static Proc/*!*/ PackingBlock(RubyContext/*!*/ context, Func<BlockParam, object, object>/*!*/ body) {
            // BlockDispatcherUnsplatN never shrinks the leading-parameter array below the count the
            // caller passed, so `__` can still carry a stale slot even though the declared parameter
            // count is 0. `args` holds every yielded value regardless, so `__` is simply ignored.
            return Proc.Create(context, 0, delegate(BlockParam/*!*/ selfBlock, object _, object[] __, RubyArray/*!*/ args) {
                return body(selfBlock, PackValues(args));
            });
        }

        /// <summary>
        /// The same block for the methods that hand a yield's values straight on to a user block.
        /// MRI forwards them unchanged there - `def each; yield 1, 2; end` makes `map { |a| a }`
        /// see 1, not [1, 2] - even where the same method packs the value it *stores*
        /// (`take_while` does both). <paramref name="body"/> gets the raw argument list.
        /// </summary>
        internal static Proc/*!*/ SplattingBlock(RubyContext/*!*/ context, Func<BlockParam, RubyArray, object>/*!*/ body) {
            return Proc.Create(context, 0, delegate(BlockParam/*!*/ selfBlock, object _, object[] __, RubyArray/*!*/ args) {
                return body(selfBlock, args);
            });
        }

        /// <summary>
        /// A <see cref="SplattingBlock(RubyContext, Func{BlockParam, RubyArray, object})"/> that reports the given arity.
        /// </summary>
        internal static Proc/*!*/ SplattingBlock(RubyContext/*!*/ context, Func<BlockParam, RubyArray, object>/*!*/ body, int arity) {
            return Proc.Create(context, 0, BlockDispatcher.MakeAttributes(BlockSignatureAttributes.HasUnsplatParameter, arity),
                (Func<BlockParam, object, object[], RubyArray, object>)delegate(BlockParam/*!*/ selfBlock, object _, object[] __, RubyArray/*!*/ args) {
                    return body(selfBlock, args);
                }
            );
        }

        #region all?, any?, none?

        [RubyMethod("all?")]
        public static object TrueForAll(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return TrueForItems(each, predicate, self, false, false);
        }

        [RubyMethod("none?")]
        public static object TrueForNone(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return TrueForItems(each, predicate, self, true, false);
        }

        [RubyMethod("any?")]
        public static object TrueForAny(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return TrueForItems(each, predicate, self, true, true);
        }

        private static object TrueForItems(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self, bool stop, bool positiveResult) {
            // An Array's #all?, #any? and #none? are its own in MRI (rb_ary_all_p etc.): they walk the
            // array and yield each element once rather than going through #each and a block of ours.
            var array = self as RubyArray;
            if (array != null) {
                // -X:OSR: once the block is hot, the rest of a plain Array's search runs in a typed copy of
                // the loop this and the block make together (BlockLoopSite)
                var loop = (predicate != null && array.Count >= BlockLoopSite.MinRun && array.GetType() == typeof(RubyArray))
                    ? BlockLoopSite.For(predicate, stop ? BlockLoopKind.FindTruthy : BlockLoopKind.FindFalsy) : null;
                long countdown = (loop != null) ? loop.Countdown : Int64.MaxValue;
                for (int i = 0; i < array.Count; i++) {
                    if (--countdown <= 0 && array.Count - i >= BlockLoopSite.MinRun) {
                        bool found;
                        i = loop.Run(predicate, array, i, ref countdown, out found);
                        if (found) { return ScriptingRuntimeHelpers.BooleanToObject(positiveResult); }
                        if (i >= array.Count) { break; }
                    }
                    object item = array[i];
                    if (predicate != null) {
                        object blockResult;
                        if (predicate.Yield(item, out blockResult)) {
                            return blockResult;
                        }
                        item = blockResult;
                    }
                    if (Protocols.IsTrue(item) == stop) {
                        if (loop != null) { loop.Leave(countdown); }
                        return ScriptingRuntimeHelpers.BooleanToObject(positiveResult);
                    }
                }
                if (loop != null) { loop.Leave(countdown); }
                return ScriptingRuntimeHelpers.BooleanToObject(!positiveResult);
            }

            object result = ScriptingRuntimeHelpers.BooleanToObject(!positiveResult);
            Each(each, self, SplattingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, RubyArray/*!*/ args) {
                object item = PackValues(args);
                if (predicate != null) {
                    object blockResult;
                    if (predicate.YieldSplat(args, out blockResult)) {
                        result = blockResult;
                        return selfBlock.PropagateFlow(predicate, blockResult);
                    }
                    item = blockResult;
                }

                bool isTrue = Protocols.IsTrue(item);
                if (isTrue == stop) {
                    result = ScriptingRuntimeHelpers.BooleanToObject(positiveResult); 
                    return selfBlock.Break(result);
                }

                return null;
            }));

            return result;
        }

        #endregion

        #region collect, map

        [RubyMethod("collect")]
        [RubyMethod("map")]
        public static Enumerator/*!*/ GetMapEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam collector, object self) {
            return new Enumerator(self, "map");
        }

        /// <summary>
        /// <code>
        /// def map
        ///   result = []
        ///   each do |*args|
        ///     result.push yield(*args)
        ///   end    
        ///   result
        /// end
        /// </code>
        /// </summary>
        [RubyMethod("collect")]
        [RubyMethod("map")]
        public static object Map(CallSiteStorage<Func<CallSite, RubyScope, object, Proc, object>>/*!*/ each, RubyScope/*!*/ scope,
            [NotNull]BlockParam/*!*/ collector, object self) {
            RubyArray resultArray = new RubyArray();
            object result = resultArray;

            // #map keeps nothing but what the block returns, so the yielded values go to the
            // block exactly as they were yielded - no packing (`yield 1, 2` reaches `{ |a| }` as 1).
            // MRI's collect_i block takes on the arity of the given block, which #each can see -
            // Hash#each yields key and value separately to a block that needs two.
            var block = SplattingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, RubyArray/*!*/ args) {
                object blockResult;
                if (collector.YieldSplat(args, out blockResult)) {
                    result = blockResult;
                    return selfBlock.PropagateFlow(collector, blockResult);
                }
                resultArray.Add(blockResult);
                return null;
            }, ProcOps.GetArity(collector.Proc));

            // #each runs with the frame that called #map, so a Regexp match it makes (an
            // enumerator over String#scan) lands in the $~ the block reads, as in MRI.
            var site = each.GetCallSite("each", new RubyCallSignature(0, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock));
            site.Target(site, scope, self, block);
            return result;
        }

        #endregion

        #region detect, find, find_index

        [RubyMethod("detect")]
        [RubyMethod("find")]
        public static Enumerator/*!*/ GetFindEnumerator(CallSiteStorage<EachSite>/*!*/ each, CallSiteStorage<Func<CallSite, object, object>>/*!*/ callStorage,
            BlockParam predicate, object self, [Optional]object ifNone) {
            return new Enumerator((_, block) => Find(each, callStorage, block, self, ifNone));
        }

        [RubyMethod("detect")]
        [RubyMethod("find")]
        public static object Find(CallSiteStorage<EachSite>/*!*/ each, CallSiteStorage<Func<CallSite, object, object>>/*!*/ callStorage,
            [NotNull]BlockParam/*!*/ predicate, object self, [Optional]object ifNone) {
            object result = Missing.Value;

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                object blockResult;
                if (predicate.Yield(item, out blockResult)) {
                    result = blockResult;
                    return selfBlock.PropagateFlow(predicate, blockResult);
                }

                if (Protocols.IsTrue(blockResult)) {
                    result = item;
                    return selfBlock.Break(item);
                }

                return null;
            }));

            if (result == Missing.Value) {
                if (ifNone == Missing.Value || ifNone == null) {
                    return null;
                }

                var site = callStorage.GetCallSite("call", 0);
                result = site.Target(site, ifNone);
            }
            return result;
        }

        [RubyMethod("find_index")]
        public static Enumerator/*!*/ GetFindIndexEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return new Enumerator((_, block) => FindIndex(each, block, self));
        }

        [RubyMethod("find_index")]
        public static object FindIndex(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ predicate, object self) {
            int index = 0;

            object result = null;
            Each(each, self, SplattingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, RubyArray/*!*/ args) {
                object blockResult;
                if (predicate.YieldSplat(args, out blockResult)) {
                    result = blockResult;
                    return selfBlock.PropagateFlow(predicate, blockResult);
                }

                if (Protocols.IsTrue(blockResult)) {
                    result = ScriptingRuntimeHelpers.Int32ToObject(index);
                    return selfBlock.Break(null);
                }

                index++;
                return null;
            }));

            return result;
        }

        [RubyMethod("find_index")]
        public static object FindIndex(CallSiteStorage<EachSite>/*!*/ each, BinaryOpStorage/*!*/ equals, BlockParam predicate, object self, object value) {
            if (predicate != null) {
                each.Context.ReportWarning("given block not used");
            }

            int index = 0;
            object result = null;
            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (Protocols.IsEqual(equals, item, value)) {
                    result = ScriptingRuntimeHelpers.Int32ToObject(index);
                    return selfBlock.Break(null);
                }

                index++;
                return null;
            }));

            return result;
        }

        #endregion

        #region each_with_index

        [RubyMethod("each_with_index")]
        public static Enumerator/*!*/ GetEachWithIndexEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam block, object self) {
            return new Enumerator(self, "each_with_index");
        }

        [RubyMethod("each_with_index")]
        public static object EachWithIndex(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ block, object self) {
            int index = 0;
            object result = self;

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                object blockResult;
                if (block.Yield(item, index, out blockResult)) {
                    result = blockResult;
                    return selfBlock.PropagateFlow(block, blockResult);
                }
                index += 1;
                return null;
            }));

            return result;
        }

        #endregion

        #region entries, to_a

        [RubyMethod("entries")]
        [RubyMethod("to_a")]
        public static RubyArray/*!*/ ToArray(CallSiteStorage<EachSite>/*!*/ each, object self) {
            RubyArray data = new RubyArray();

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                data.Add(item);
                return null;
            }));

            return data;
        }

        [RubyMethod("entries")]
        [RubyMethod("to_a")]
        public static RubyArray/*!*/ ToArray(CallSiteStorage<EachSiteN>/*!*/ each, object self, params object[] args) {
            RubyArray data = new RubyArray();

            Each(each, self, args, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                data.Add(item);
                return null;
            }));

            return data;
        }

        #endregion

        #region find_all, select, reject

        [RubyMethod("find_all")]
        [RubyMethod("select")]
        public static object Select(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return (predicate != null) ? FilterImpl(each, predicate, self, true) : FilterEnum(each, predicate, self, true);
        }

        [RubyMethod("reject")]
        public static object Reject(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return (predicate != null) ? FilterImpl(each, predicate, self, false) : FilterEnum(each, predicate, self, false);
        }

        private static Enumerator/*!*/ FilterEnum(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self, bool acceptingValue) {
            return new Enumerator(self, acceptingValue ? "select" : "reject");
        }

        private static object FilterImpl(CallSiteStorage<EachSite>/*!*/ each, BlockParam/*!*/ predicate, object self, bool acceptingValue) {
            RubyArray resultArray = new RubyArray();
            object result = resultArray;

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                object blockResult;
                if (predicate.Yield(item, out blockResult)) {
                    result = blockResult;
                    return selfBlock.PropagateFlow(predicate, blockResult);
                }

                // Check if the result is what we expect (use true to select, false to reject)
                if (Protocols.IsTrue(blockResult) == acceptingValue) {
                    resultArray.Add(item);
                }
                return null;
            }));

            return result;
        }

        #endregion

        #region grep

        [RubyMethod("grep")]
        public static object Grep(CallSiteStorage<EachSite>/*!*/ each, BinaryOpStorage/*!*/ caseEquals,
            ConversionStorage<MutableString>/*!*/ stringTryCast, RubyScope/*!*/ scope,
            BlockParam action, object self, object pattern) {
            return Grep(each, caseEquals, stringTryCast, scope, action, self, pattern, true);
        }

        [RubyMethod("grep_v")]
        public static object GrepV(CallSiteStorage<EachSite>/*!*/ each, BinaryOpStorage/*!*/ caseEquals,
            ConversionStorage<MutableString>/*!*/ stringTryCast, RubyScope/*!*/ scope,
            BlockParam action, object self, object pattern) {
            return Grep(each, caseEquals, stringTryCast, scope, action, self, pattern, false);
        }

        private static object Grep(CallSiteStorage<EachSite>/*!*/ each, BinaryOpStorage/*!*/ caseEquals,
            ConversionStorage<MutableString>/*!*/ stringTryCast, RubyScope/*!*/ scope,
            BlockParam action, object self, object pattern, bool accepting) {

            RubyArray resultArray = new RubyArray();
            object result = resultArray;
            var site = caseEquals.GetCallSite("===");

            // With a block, MRI's Regexp#=== stores each match (or nil) as the caller's $~, which
            // the block shares; without one it matches without touching $~.
            RubyRegex regex = (action != null) ? pattern as RubyRegex : null;

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                bool matches = (regex != null)
                    ? RegexpOps.CaseCompare(stringTryCast, scope, regex, item)
                    : RubyOps.IsTrue(site.Target(site, pattern, item));

                if (matches == accepting) {
                    if (action != null && action.Yield(item, out item)) {
                        result = item;
                        return selfBlock.PropagateFlow(action, item);
                    }
                    resultArray.Add(item);
                }
                return null;
            }));

            return result;
        }

        #endregion

        #region include?, member?

        [RubyMethod("include?")]
        [RubyMethod("member?")]
        public static object Contains(CallSiteStorage<EachSite>/*!*/ each, BinaryOpStorage/*!*/ equals, object self, object value) {
            object result = ScriptingRuntimeHelpers.BooleanToObject(false);

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (Protocols.IsEqual(equals, item, value)) {
                    result = ScriptingRuntimeHelpers.BooleanToObject(true);
                    return selfBlock.Break(result);
                }
                return null;
            }));

            return result;
        }

        #endregion

        #region inject/reduce

        [RubyMethod("reduce")]
        [RubyMethod("inject")]
        public static object Inject(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ operation, object self, [Optional]object initial) {
            return Inject(each, operation, null, null, self, initial);
        }

        [RubyMethod("reduce")]
        [RubyMethod("inject")]
        public static object Inject(CallSiteStorage<EachSite>/*!*/ each, RubyScope/*!*/ scope, object self, [Optional]object initial, 
            [DefaultProtocol, NotNull]string/*!*/ operatorName) {

            return Inject(each, null, scope, operatorName, self, initial);
        }

        // def inject(result = Undefined)
        //   each do |*args|
        //     arg = args.size <= 1 ? args[0] : args
        //     if result == Undefined
        //       result = arg
        //     else
        //       result = yield(result, arg)
        //     end
        //   end
        //   result
        // end
        internal static object Inject(CallSiteStorage<EachSite>/*!*/ each, BlockParam operation, RubyScope scope, string operatorName, object self, object initial) {
            Debug.Assert(operation != null ^ (scope != null && operatorName != null));

            var site = (operatorName == null) ? null : each.Context.GetOrCreateSendSite<Func<CallSite, RubyScope, object, object, object>>(
                operatorName, new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );

            var list = self as RubyArray;
            if (list != null && IsPlainList(each.Context, self)) {
                return InjectList(list, operation, scope, operatorName, site, initial);
            }

            object result = initial;
            Each(each, self, Proc.Create(each.Context, 0, delegate(BlockParam/*!*/ selfBlock, object _, object[] __, RubyArray/*!*/ args) {
                Debug.Assert(__.Length == 0);

                // TODO: this is weird but is actually exploited in Rack::Utils::HeaderHash
                // TODO: Can we optimize (special dispatcher)? We allocate splatte array for each iteration.
                object value = args.Count == 0 ? null : args.Count == 1 ? args[0] : args;

                if (result == Missing.Value) {
                    result = value;
                    return null;
                }

                if (site != null) {
                    result = site.Target(site, scope, result, value);
                } else if (operation.Yield(result, value, out result)) {
                    return selfBlock.PropagateFlow(operation, result);
                }

                return null;
            }));

            return result != Missing.Value ? result : null;
        }

        /// <summary>
        /// #inject over a plain list (see IsPlainList): the elements are taken from the list,
        /// with no #each call, Proc or argument array for each one. inject(:+) over Fixnums adds
        /// them here while Integer#+ is the built-in, as MRI's ary_inject_op does, and goes back to
        /// calling #+ at the first element that is not a Fixnum.
        /// </summary>
        private static object InjectList(RubyArray/*!*/ list, BlockParam operation, RubyScope scope, string operatorName,
            CallSite<Func<CallSite, RubyScope, object, object, object>> site, object initial) {

            object result = initial;
            int i = 0;
            if (result == Missing.Value) {
                if (list.Count == 0) {
                    return null;
                }
                result = list[0];
                i = 1;
            }

            if (site != null && operatorName == "+" && IListOps.IsBuiltinMethod(scope.RubyContext.IntegerClass, "+")) {
                for (; i < list.Count && result is int && list[i] is int; i++) {
                    result = ClrInteger.Add((int)result, (int)list[i]);
                }
            }

            for (; i < list.Count; i++) {
                object value = list[i];
                if (site != null) {
                    result = site.Target(site, scope, result, value);
                } else if (operation.Yield(result, value, out result)) {
                    return result;
                }
            }
            return result;
        }

        #endregion

        #region min, max, minmax

        [RubyMethod("max")]
        public static object GetMaximum(CallSiteStorage<EachSite>/*!*/ each, ComparisonStorage/*!*/ comparisonStorage, BlockParam comparer, object self) {
            return GetExtreme(each, comparisonStorage, comparer, self, +1);
        }

        [RubyMethod("min")]
        public static object GetMinimum(CallSiteStorage<EachSite>/*!*/ each, ComparisonStorage/*!*/ comparisonStorage, BlockParam comparer, object self) {
            return GetExtreme(each, comparisonStorage, comparer, self, -1);
        }

        private static object GetExtreme(CallSiteStorage<EachSite>/*!*/ each, ComparisonStorage/*!*/ comparisonStorage, BlockParam comparer, object self, int comparisonValue) {
            bool firstItem = true;
            object result = null;

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (firstItem) {
                    result = item;
                    firstItem = false;
                    return null;
                }

                object blockResult;
                int? compareResult = CompareItems(comparisonStorage, item, result, comparer, out blockResult);
                if (compareResult == null) {
                    result = blockResult;
                    return selfBlock.PropagateFlow(comparer, blockResult);
                }

                // Check if we have found the new minimum or maximum (+1 to select max, -1 to select min)
                if (compareResult == comparisonValue) {
                    result = item;
                }

                return null;
            }));

            return result;
        }

        [RubyMethod("minmax")]
        public static object GetExtremes(CallSiteStorage<EachSite>/*!*/ each, ComparisonStorage/*!*/ comparisonStorage, BlockParam comparer, object self) {
            bool hasOddItem = false, hasMinMax = false, blockJumped = false;
            object oddItem = null;
            object blockResult = null;

            object min = null, max = null;

            Func<BlockParam, object, object> blockProc = delegate(BlockParam/*!*/ selfBlock, object item) {
                if (hasOddItem) {
                    hasOddItem = false;

                    int? compareResult = CompareItems(comparisonStorage, oddItem, item, comparer, out blockResult);
                    if (compareResult == null) {
                        goto BlockJumped;
                    }
                    if (compareResult > 0) {
                        // oddItem > item
                        object obj = item;
                        item = oddItem;
                        oddItem = obj;
                    }

                    // oddItem <= item
                    if (hasMinMax) {
                        compareResult = CompareItems(comparisonStorage, oddItem, min, comparer, out blockResult);
                        if (compareResult == null) {
                            goto BlockJumped;
                        }
                        if (compareResult < 0) {
                            // oddItem < min
                            min = oddItem;
                        }

                        compareResult = CompareItems(comparisonStorage, item, max, comparer, out blockResult);
                        if (compareResult == null) {
                            goto BlockJumped;
                        }
                        if (compareResult > 0) {
                            // item > max
                            max = item;
                        }
                    } else {
                        min = oddItem;
                        max = item;
                        hasMinMax = true;
                    }
                } else {
                    hasOddItem = true;
                    oddItem = item;
                }

                return null;

            BlockJumped:
                blockJumped = true;
                return selfBlock.PropagateFlow(comparer, blockResult);
            };

            Each(each, self, PackingBlock(each.Context, blockProc));

            if (blockJumped) {
                return blockResult;
            }

            if (!hasMinMax) {
                return hasOddItem ? new RubyArray(2) { oddItem, oddItem } : new RubyArray(2) { null, null };
            }

            if (hasOddItem) {
                int? compareResult = CompareItems(comparisonStorage, oddItem, min, comparer, out blockResult);
                if (compareResult == null) {
                    return blockResult;
                }
                if (compareResult < 0) {
                    min = oddItem;
                }

                compareResult = CompareItems(comparisonStorage, oddItem, max, comparer, out blockResult);
                if (compareResult == null) {
                    return blockResult;
                }
                if (compareResult > 0) {
                    max = oddItem;
                }
            }

            return new RubyArray(2) { min, max };
        }

        private static int? CompareItems(ComparisonStorage/*!*/ comparisonStorage, object left, object right, BlockParam comparer, out object blockResult) {
            if (comparer != null) {
                if (comparer.Yield(left, right, out blockResult)) {
                    return null;
                }

                if (blockResult == null) {
                    throw RubyExceptions.MakeComparisonError(comparisonStorage.Context, left, right);
                }

                return Protocols.ConvertCompareResult(comparisonStorage, blockResult);
            } else {
                blockResult = null;
                return Protocols.Compare(comparisonStorage, left, right);
            }
        }

        #endregion

        #region partition

        [RubyMethod("partition")]
        public static Enumerator/*!*/ GetPartitionEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return new Enumerator(self, "partition");
        }

        [RubyMethod("partition")]
        public static object Partition(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ predicate, object self) {
            RubyArray trueSet = new RubyArray();
            RubyArray falseSet = new RubyArray();
            RubyArray pair = new RubyArray(2);
            pair.Add(trueSet);
            pair.Add(falseSet);
            object result = pair;

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                object blockResult;
                if (predicate.Yield(item, out blockResult)) {
                    result = blockResult;
                    return selfBlock.PropagateFlow(predicate, blockResult);
                }

                if (Protocols.IsTrue(blockResult)) {
                    trueSet.Add(item);
                } else {
                    falseSet.Add(item);
                }

                return null;
            }));

            return result;
        }

        #endregion

        #region sort, sort_by

        [RubyMethod("sort")]
        public static object Sort(CallSiteStorage<EachSite>/*!*/ each, ComparisonStorage/*!*/ comparisonStorage, BlockParam keySelector, object self) {
            return ArrayOps.SortInPlace(comparisonStorage, keySelector, ToArray(each, self));
        }

        [RubyMethod("sort_by")]
        public static object SortBy(CallSiteStorage<EachSite>/*!*/ each, ComparisonStorage/*!*/ comparisonStorage, BlockParam keySelector, object self) {
            // collect key, value pairs
            List<KeyValuePair<object, object>> keyValuePairs = new List<KeyValuePair<object, object>>();
            object result = null;

            // Collect the key, value pairs
            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (keySelector == null) {
                    throw RubyExceptions.NoBlockGiven();
                }

                object key;
                if (keySelector.Yield(item, out key)) {
                    keyValuePairs = null;
                    result = key;
                    return selfBlock.PropagateFlow(keySelector, key);
                }

                keyValuePairs.Add(new KeyValuePair<object, object>(key, item));
                return null;
            }));

            if (keyValuePairs == null) {
                return result;
            }

            // sort by keys
            keyValuePairs.Sort(delegate(KeyValuePair<object, object> x, KeyValuePair<object, object> y) {
                return Protocols.Compare(comparisonStorage, x.Key, y.Key);
            });

            // return values
            RubyArray results = new RubyArray(keyValuePairs.Count);
            foreach (KeyValuePair<object, object> pair in keyValuePairs) {
                results.Add(pair.Value);
            }

            return results;
        }

        #endregion

        #region zip

        [RubyMethod("zip")]
        public static object Zip(CallSiteStorage<EachSite>/*!*/ each, BlockParam block, object self, [DefaultProtocol, NotNullItems]params IList/*!*/[]/*!*/ args) {
            RubyArray results = (block == null) ? new RubyArray() : null;
            object result = results;

            int index = 0;
            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                // Collect items
                RubyArray array = new RubyArray(args.Length + 1);
                array.Add(item);
                foreach (IList otherArray in args) {
                    if (index < otherArray.Count) {
                        array.Add(otherArray[index]);
                    } else {
                        array.Add(null);
                    }
                }

                index += 1;

                if (block != null) {
                    object blockResult;
                    if (block.Yield(array, out blockResult)) {
                        result = blockResult;
                        return selfBlock.PropagateFlow(block, blockResult);
                    }
                } else {
                    results.Add(array);
                }
                return null;
            }));

            return result;
        }

        #endregion

        #region count, one?

        [RubyMethod("count")]
        public static int Count(CallSiteStorage<EachSite>/*!*/ each, object self) {
            int result = 0;
            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                result++;
                return null;
            }));
            return result;
        }

        [RubyMethod("count")]
        public static int Count(CallSiteStorage<EachSite>/*!*/ each, BinaryOpStorage/*!*/ equals, BlockParam comparer, object self, object value) {
            if (comparer != null) {
                each.Context.ReportWarning("given block not used");
            }

            int result = 0;
            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (Protocols.IsEqual(equals, item, value)) {
                    result++;
                }
                return null;
            }));
            return result;
        }

        [RubyMethod("count")]
        public static object Count(CallSiteStorage<EachSite>/*!*/ each, BinaryOpStorage/*!*/ equals, [NotNull]BlockParam/*!*/ comparer, object self) {
            int count = 0;

            object result = null;
            Each(each, self, SplattingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, RubyArray/*!*/ args) {
                object blockResult;
                if (comparer.YieldSplat(args, out blockResult)) {
                    count = -1;
                    result = blockResult;
                    return selfBlock.PropagateFlow(comparer, blockResult); 
                }

                if (Protocols.IsTrue(blockResult)) {
                    count++;
                }
                return null;
            }));

            return (count >= 0) ? ScriptingRuntimeHelpers.Int32ToObject(count) : result;
        }

        [RubyMethod("one?")]
        public static object One(CallSiteStorage<EachSite>/*!*/ each, BinaryOpStorage/*!*/ equals, BlockParam comparer, object self) {
            int count = 0;

            object result = null;
            Each(each, self, SplattingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, RubyArray/*!*/ args) {
                object blockResult;
                if (comparer == null) {
                    blockResult = PackValues(args);
                } else if (comparer.YieldSplat(args, out blockResult)) {
                    count = -1;
                    result = blockResult;
                    return selfBlock.PropagateFlow(comparer, blockResult);
                }

                if (Protocols.IsTrue(blockResult) && ++count > 1) {
                    selfBlock.Break(null);
                }
                return null;
            }));

            return (count >= 0) ? ScriptingRuntimeHelpers.BooleanToObject(count == 1) : result;
        }

        #endregion

        #region first, take, take_while, drop, drop_while, cycle

        [RubyMethod("first")]
        public static object First(CallSiteStorage<EachSite>/*!*/ each, object self) {
            object result = null;
            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                result = item;
                selfBlock.Break(null);
                return null;
            }));
            return result;
        }

        [RubyMethod("first")]
        [RubyMethod("take")]
        public static RubyArray/*!*/ Take(CallSiteStorage<EachSite>/*!*/ each, object self, [DefaultProtocol]int count) {
            if (count < 0) {
                throw RubyExceptions.CreateArgumentError("attempt to take negative size");
            }

            var result = new RubyArray(count);
            if (count == 0) {
                return result;
            }

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                result.Add(item);
                if (--count == 0) {
                    selfBlock.Break(null);
                }
                return null;
            }));

            return result;
        }

        [RubyMethod("take_while")]
        public static Enumerator/*!*/ GetTakeWhileEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return new Enumerator(self, "take_while");
        }

        [RubyMethod("take_while")]
        public static object TakeWhile(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ predicate, object self) {
            RubyArray resultArray = new RubyArray();

            object result = resultArray;
            Each(each, self, SplattingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, RubyArray/*!*/ args) {
                object blockResult;
                if (predicate.YieldSplat(args, out blockResult)) {
                    result = blockResult;
                    return selfBlock.PropagateFlow(predicate, blockResult);
                }

                if (Protocols.IsTrue(blockResult)) {
                    resultArray.Add(PackValues(args));
                } else {
                    selfBlock.Break(null);
                }

                return null;
            }));

            return result;
        }

        [RubyMethod("drop")]
        public static RubyArray/*!*/ Drop(CallSiteStorage<EachSite>/*!*/ each, object self, [DefaultProtocol]int count) {
            if (count < 0) {
                throw RubyExceptions.CreateArgumentError("attempt to drop negative size");
            } 
            
            var result = new RubyArray();

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (count > 0) {
                    count--;
                } else {
                    result.Add(item);
                }

                return null;
            }));

            return result;
        }

        [RubyMethod("drop_while")]
        public static Enumerator/*!*/ GetDropWhileEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, object self) {
            return new Enumerator(self, "drop_while");
        }

        [RubyMethod("drop_while")]
        public static object DropWhile(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ predicate, object self) {
            RubyArray resultArray = new RubyArray();

            bool dropping = true;
            object result = resultArray; 
            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (dropping) {
                    object blockResult;
                    if (predicate.Yield(item, out blockResult)) {
                        result = blockResult;
                        return selfBlock.PropagateFlow(predicate, blockResult);
                    }

                    dropping = Protocols.IsTrue(blockResult);
                }

                if (!dropping) {
                    resultArray.Add(item);
                }

                return null;
            }));

            return result;
        }

        [RubyMethod("cycle")]
        public static Enumerator/*!*/ GetCycleEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam block, object self,
            [DefaultProtocol, DefaultParameterValue(Int32.MaxValue)]int iterations) {
            // Int32.MaxValue is the "no argument" sentinel, which means an endless cycle rather
            // than that many repetitions - so the size descriptor gets nil for it, not the number.
            return Enumerator.Sized((_, innerBlock) => Cycle(each, innerBlock, self, iterations), self, "cycle",
                iterations == Int32.MaxValue ? null : ScriptingRuntimeHelpers.Int32ToObject(iterations));
        }

        [RubyMethod("cycle")]
        public static object Cycle(CallSiteStorage<EachSite>/*!*/ each, BlockParam block, object self, DynamicNull iterations) {
            return (block != null) ? Cycle(each, block, self, Int32.MaxValue) : GetCycleEnumerator(each, block, self, Int32.MaxValue);
        }

        [RubyMethod("cycle")]
        public static object Cycle(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ block, object self,
            [DefaultProtocol, DefaultParameterValue(Int32.MaxValue)]int iterations) {

            if (iterations <= 0) {
                return null;
            }

            List<object> items = (iterations > 1) ? new List<object>() : null;

            // call "each" only in the first iteration:
            object result = null;
            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (block.Yield(item, out result)) {
                    iterations = -1;
                    return selfBlock.PropagateFlow(block, result);
                }
                if (items != null) {
                    items.Add(item);
                }
                return null;
            }));

            // An empty collection has nothing to cycle over: MRI returns nil immediately.
            // Without this the iterations == Int32.MaxValue branch below spins forever,
            // since the inner foreach never runs and never breaks out.
            if (items == null || items.Count == 0) {
                return result;
            }

            // the rest of the iterations read from cached values:
            while (iterations == Int32.MaxValue || --iterations > 0) {
                foreach (var item in items) {
                    if (block.Yield(item, out result)) {
                        return result;
                    }
                }
            }

            return result;
        }

        #endregion

        #region each_cons, each_slice

        [RubyMethod("each_cons")]
        public static Enumerator/*!*/ GetEachConsEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam block, object self, [DefaultProtocol]int sliceSize) {
            return new Enumerator(self, "each_cons", sliceSize);
        }

        [RubyMethod("each_cons")]
        public static object EachCons(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ block, object self, [DefaultProtocol]int sliceSize) {
            return EachSlice(each, block, self, sliceSize, false, (slice) => {
                RubyArray newSlice = new RubyArray(slice.Count);
                for (int i = 1; i < slice.Count; i++) {
                    newSlice.Add(slice[i]);
                }
                return newSlice;
            });
        }

        [RubyMethod("each_slice")]
        public static Enumerator/*!*/ GetEachSliceEnumerator(CallSiteStorage<EachSite>/*!*/ each, BlockParam block, object self, [DefaultProtocol]int sliceSize) {
            return new Enumerator(self, "each_slice", sliceSize);
        }

        [RubyMethod("each_slice")]
        public static object EachSlice(CallSiteStorage<EachSite>/*!*/ each, [NotNull]BlockParam/*!*/ block, object self, [DefaultProtocol]int sliceSize) {
            return EachSlice(each, block, self, sliceSize, true, (slice) => null);
        }

        private static object EachSlice(CallSiteStorage<EachSite>/*!*/ each, BlockParam/*!*/ block, object self, int sliceSize,
            bool includeIncomplete, Func<RubyArray/*!*/, RubyArray>/*!*/ newSliceFactory) {

            if (sliceSize <= 0) {
                throw RubyExceptions.CreateArgumentError("invalid slice size");
            }

            RubyArray slice = null;

            object result = null;

            Each(each, self, PackingBlock(each.Context, delegate(BlockParam/*!*/ selfBlock, object item) {
                if (slice == null) {
                    slice = new RubyArray(sliceSize);
                }

                slice.Add(item);

                if (slice.Count == sliceSize) {
                    var completeSlice = slice;
                    slice = newSliceFactory(slice);

                    object blockResult;
                    if (block.Yield(completeSlice, out blockResult)) {
                        result = blockResult;
                        return selfBlock.PropagateFlow(block, blockResult);
                    }
                }

                return null;
            }));

            if (slice != null && includeIncomplete) {
                object blockResult;
                if (block.Yield(slice, out blockResult)) {
                    return blockResult;
                }
            }

            return result;
        }

        #endregion
    }
}

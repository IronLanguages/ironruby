/* ****************************************************************************
 *
 * -X:OSR - on-stack replacement for Ruby loops.
 *
 * The DLR tiers up per *invocation*: a LightLambda compiles itself after ~32 calls. A hot
 * loop inside a body that is entered once therefore stays interpreted forever, which is the
 * shape a Ruby script's main loop has - and the shape of every loop in Util/bench.
 *
 * What makes replacing such a loop cheap here is where IronRuby keeps Ruby locals: in one
 * MutableTuple per scope, which the RubyScope points at. They are not in the frame, so there
 * is no frame state to materialize. A loop can be abandoned at a back edge, its tuples
 * handed to a compiled copy, and the copy started at the top of the next iteration.
 *
 * What is worth replacing it *with* is the other half of the story. Handing the loop's own
 * expression tree to the DLR's compiler buys almost nothing - measured on Util/bench,
 * IronRuby's compiled tier runs an arithmetic loop at the same speed as its interpreter,
 * because the cost is the dynamic call site on every `+' and every `<', not the
 * interpretation. So the copy this asks for is a *type-specialized* one (JitLoopCompiler),
 * with the loop's locals in CLR int/double locals and a Fixnum add compiled to an add. That
 * is worth 3-7x, and when the loop is not the shape that can have one, the site retires and
 * the loop runs exactly as it did before.
 *
 * The counter itself lives in the loop, in a CLR local, and costs two compares and a
 * decrement per iteration. The site's Countdown field is only read on the way in and written
 * on the way out, so a loop entered many times for a few iterations each still adds up.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Jit;

namespace IronRuby.Runtime.Osr {
    /// <summary>
    /// One per `while' loop in the compiled tree, shared by every execution of that loop.
    /// </summary>
    public sealed class OsrLoopSite {
        /// <summary>
        /// What Run answers when it has no compiled copy to offer, or when the copy it had
        /// gave the loop back. A Ruby loop can never produce this object, so the test at the
        /// back edge is an unambiguous reference comparison.
        /// </summary>
        public static readonly object/*!*/ Retry = new object();

        /// <summary>
        /// What a compiled copy answers when a local does not hold the CLR type it was built
        /// for. Almost always that is an accumulator that has grown out of Int32 and is now an
        /// Int64, so a copy built over the types the locals hold *now* would run: the site
        /// throws this one away and builds another, a bounded number of times.
        /// </summary>
        public static readonly object/*!*/ Retype = new object();

        /// <summary>
        /// What a compiled copy answers when the loop is entered under other refinements than the
        /// ones it was built under (an eval string's, a `def' run again after a `using'). The types
        /// under it have not moved, so this does not count against MaxRetypes: the site builds a
        /// copy for the refinements in force now, up to MaxRefinementRebuilds times.
        /// </summary>
        public static readonly object/*!*/ RefinementsChanged = new object();

        /// <summary>
        /// Back edges the generic loop runs before it asks again, after a copy handed it back.
        /// Long enough that a loop deopting every iteration costs its eight rounds and no more,
        /// short enough that the tail of a loop whose accumulator just widened is not lost.
        /// </summary>
        private const long ReArm = 64;

        /// <summary>Copies one loop may be rebuilt for, after the types under it moved.</summary>
        private const int MaxRetypes = 3;

        /// <summary>
        /// Copies one loop may be rebuilt for, after the refinements it runs under changed. Each is a
        /// compilation, so a loop entered under alternating refinements stops paying for them in the end.
        /// </summary>
        private const int MaxRefinementRebuilds = 8;

        /// <summary>IR_OSR_VERBOSE=1 traces which loops get specialized and what that cost.</summary>
        public static readonly bool Verbose = Environment.GetEnvironmentVariable("IR_OSR_VERBOSE") == "1";

        /// <summary>
        /// Back edges a loop may take before it is replaced. Low enough that a script's main
        /// loop is specialized almost immediately, high enough that a loop which merely runs a
        /// few hundred times is never worth a compilation (~8ms).
        /// </summary>
        internal static readonly long Threshold = ReadThreshold();

        private static long ReadThreshold() {
            long v;
            var s = Environment.GetEnvironmentVariable("IR_OSR_THRESHOLD");
            return (s != null && Int64.TryParse(s, out v) && v > 0) ? v : 2000;
        }

        internal static int Specialized, Retired, Entered, Deopts;
        internal static long CompileTicks;

        /// <summary>
        /// Back edges left before the next entry of this loop asks for a compiled copy. Read
        /// at loop entry into a local and written back at a normal exit.
        /// </summary>
        public long Countdown = Threshold;

        /// <summary>False once the site has settled: the exits stop writing Countdown back.</summary>
        public bool Counting = true;

        internal static readonly System.Reflection.FieldInfo/*!*/ CountdownField =
            typeof(OsrLoopSite).GetField("Countdown");
        internal static readonly System.Reflection.FieldInfo/*!*/ CountingField =
            typeof(OsrLoopSite).GetField("Counting");
        internal static readonly System.Reflection.MethodInfo/*!*/ RunMethod =
            typeof(OsrLoopSite).GetMethod("Run");

        // What the specializing compiler needs: the loop's Ruby AST, the scope it is written
        // in, and where in the argument array each lexical depth's locals tuple sits.
        internal WhileLoopExpression LoopAst;
        internal RubyContext Context;
        internal int ScopeDepth;
        internal Dictionary<int, int> TupleArgIndex;
        // Where in the argument array the loop's self is; -1 if it is not passed.
        internal int SelfArgIndex = -1;
        // Where in the argument array the loop's scope is (its refinements); -1 if it is not passed.
        internal int ScopeArgIndex = -1;

        private readonly object/*!*/ _lock = new object();
        private readonly string/*!*/ _name;
        private volatile Func<object[], object> _typed;
        private bool _tried;
        private int _deopts;
        private int _retypes;
        private int _refinementRebuilds;

        public OsrLoopSite(string/*!*/ name) {
            _name = name;
            HookStats();
        }

        /// <summary>
        /// Called from the loop's back edge once the countdown runs out. Returns the loop's
        /// value if a specialized copy ran it to the end, or Retry to say "carry on where you
        /// are" - in which case the locals are exactly as the loop left them.
        /// </summary>
        public object Run(object[]/*!*/ tuples) {
            Entered++;
            var typed = _typed;
            if (typed == null && !_tried) {
                typed = Compile(tuples);
            }

            if (typed != null) {
                object result = typed(tuples);
                if (ReferenceEquals(result, Retype)) {
                    // A local is not the CLR type this copy was built for. The usual cause is an
                    // Integer that outgrew an Int32 and is now an Int64: build another copy over
                    // what the locals hold now, and the rest of the loop runs specialized on the
                    // wider representation instead of falling back to the generic body for good.
                    Deopts++;
                    if (++_retypes > MaxRetypes) { Retire(); } else { Rebuild(); }
                    return Retry;
                }
                if (ReferenceEquals(result, RefinementsChanged)) {
                    // Entered under other refinements than the copy's: build one for these, without
                    // spending the rebuilds a widening local may still need.
                    if (++_refinementRebuilds > MaxRefinementRebuilds) { Retire(); } else { Rebuild(); }
                    return Retry;
                }
                if (!ReferenceEquals(result, Retry)) { return result; }

                if (RubyUtils.IsSafePointRequested) {
                    // Not a failure: the copy stopped at an iteration boundary because a
                    // Thread#raise, Thread#kill, signal or finalizer is waiting
                    // (JitRuntime.OsrSafePoint). The generic loop delivers it at its next back
                    // edge; if it was for another thread, the copy is asked again shortly.
                    ReArmCountdown();
                    return Retry;
                }

                // The copy gave the loop back: an Integer overflowed out of Int64, a guard did
                // not hold, or a division would have raised. A few of those and the loop is
                // better off where it is; until then the generic body runs a short stretch and
                // the back edge asks again, which is how an iteration that widened a local gets
                // to be seen at all.
                Deopts++;
                if (++_deopts >= 8) { Retire(); } else { ReArmCountdown(); }
            } else {
                Retire();
            }
            return Retry;
        }

        /// <summary>Let the back edge fire again after a short stretch of the generic loop.</summary>
        private void ReArmCountdown() {
            Counting = false;
            Countdown = ReArm;
        }

        /// <summary>Throw the compiled copy away so the next entry builds one over current types.</summary>
        private void Rebuild() {
            lock (_lock) {
                _typed = null;
                _tried = false;
            }
            ReArmCountdown();
        }

        /// <summary>Stop counting, for good: this loop will not be replaced.</summary>
        private void Retire() {
            _typed = null;
            Counting = false;
            Countdown = Int64.MaxValue;
            Retired++;
        }

        private Func<object[], object> Compile(object[]/*!*/ tuples) {
            lock (_lock) {
                if (_tried) { return _typed; }
                _tried = true;
                if (LoopAst == null || Context == null || TupleArgIndex == null) { return null; }

                long start = Stopwatch.GetTimestamp();
                try {
                    _typed = JitLoopCompiler.TryCompile(LoopAst, Context, ScopeDepth, TupleArgIndex, tuples, SelfArgIndex, ScopeArgIndex);
                } catch (Exception e) {
                    if (Verbose) {
                        Console.Error.WriteLine("[osr] {0}: specialization failed: {1}", _name, e.Message);
                    }
                    _typed = null;
                }
                long ticks = Stopwatch.GetTimestamp() - start;
                CompileTicks += ticks;

                if (_typed != null) {
                    Specialized++;
                    // Later entries should reach the specialized copy at once, and the exits
                    // must stop handing back what this one did not spend - which would drive
                    // the countdown to zero and switch the back edge off for good.
                    Counting = false;
                    Countdown = 1;
                }
                if (Verbose) {
                    Console.Error.WriteLine("[osr] {0}: {1} in {2:F2}ms", _name,
                        _typed != null ? "specialized" : "not specializable", ticks * 1000.0 / Stopwatch.Frequency);
                }
                return _typed;
            }
        }

        private static int _statsHooked;

        /// <summary>With IR_OSR_VERBOSE=1, print what OSR did on the way out.</summary>
        internal static void HookStats() {
            if (!Verbose || System.Threading.Interlocked.Exchange(ref _statsHooked, 1) != 0) { return; }
            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                Console.Error.WriteLine("[osr] specialized={0} retired={1} entries={2} deopts={3} compile={4:F1}ms",
                    Specialized, Retired, Entered, Deopts, CompileTicks * 1000.0 / Stopwatch.Frequency);
            };
        }
    }
}

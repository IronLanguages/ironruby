/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * The profiling tier and the entry stub.
 *
 * When -X:JIT is on, RubyMethodBody.Compile wraps the generic body delegate in a
 * trampoline of the same signature, so nothing else in the runtime - the binder, the
 * call site rules, the dispatchers - has to change. The trampoline counts calls and
 * records the CLR type of each argument; once the method is hot and its argument types
 * have been stable, it asks JitCompiler for a typed body and, if it gets one, compiles
 * an *entry* lambda that checks the guards and either enters the typed body or falls
 * through to the generic one.
 *
 * Guards, and why they are sound:
 *   - RubyModule.GlobalMethodVersion is bumped by every method table change anywhere
 *     (def, undef, alias, include/extend, singleton method, attr_*), so one snapshot
 *     comparison covers method redefinition and overriding in a subclass.
 *   - JitRuntime.Disabled is set when refinements appear.
 *   - The receiver's immediate class is pinned whenever the specialized body contains a
 *     direct self-call, so that call cannot be a call to an override.
 *   - Argument types are re-checked on entry; a miss falls through to the generic body.
 *   - Integer overflow out of Int64, a narrowing back to Int32 that does not fit, and
 *     division by zero raise JitDeoptException, caught at the entry,
 *     which re-runs the generic body from the top. JitCompiler only produces code that
 *     can deopt for a body with no observable effect, so the restart is invisible.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    internal abstract class JitStub {
        internal const int Threshold = 40;
        internal const int MaxArity = 6;
        internal const string EntryNamePrefix = "jitentry$";
        internal const string DirectEntryNamePrefix = "jitdirect$";

        // What a typed body's call evaluates to in the entry when it deopted.
        private static readonly object Deopted = new object();

        protected readonly MethodDeclaration/*!*/ _ast;
        protected readonly RubyContext/*!*/ _context;
        protected readonly RubyModule/*!*/ _declaringModule;
        protected readonly Delegate/*!*/ _generic;
        protected readonly int _arity;
        // A method with keyword parameters (see JitKeywords): its last delegate parameter is the
        // keyword hash, and the typed body takes the keyword values instead, after the positionals.
        protected readonly JitKeywords _keywords;
        private readonly int _positional;     // delegate parameters that are positional parameters
        private readonly int _typedCount;     // parameters of the typed body

        // A specialized keyword method's entry for JitKeywordCall: (self, p0, p1, k0, k1, k2) with the
        // keyword values in declaration order and unused slots null. Answers JitKeywordCall.NoDirect
        // when a guard fails. Null when the method is not specialized.
        private volatile Func<object, object, object, object, object, object, object> _direct;

        internal Func<object, object, object, object, object, object, object> Direct { get { return _direct; } }
        internal JitKeywords Keywords { get { return _keywords; } }
        // Declined for good: the generic body is all this method will ever run, so there is no Direct to wait for.
        internal bool GivenUp { get { return _state == Settled; } }
        // Retired for a while (OnDeopt, Probe): the entry, and Direct with it, comes back after the cool-down.
        internal bool IsCooling { get { return _state == Cooling; } }
        internal int Positional { get { return _positional; } }

        // Optional parameters (after the mandatory ones, as in the generic delegate) have a literal
        // default; a call that omits one passes RubyOps.DefaultArgument in its place, which the
        // entry and the profiler replace with the value. _hasDefault is null with no optionals.
        private bool[] _hasDefault;
        private object[] _defaults;

        // A method with an optional parameter whose default is not such a literal gets one typed body
        // per number of optional arguments a call passes (JitCompiler's `supplied'), each evaluating the
        // defaults of the rest itself. _countsSeen has bit k set once a call passed k of them.
        private int _perCountMandatory = -1;
        private int _countsSeen;

        // Parameters the body only ever stores into an instance variable: kept as the boxed
        // objects they arrived as, whatever their type (JitScreen's otherUses).
        private bool[] _storedOnly;

        // The scope the method was defined in, which its constants resolve in (JitCompiler.EmitConstant).
        // The compiled body is shared only by `def's of the same lexical module chain, which is all a
        // constant lookup depends on (RubyMethodBody.GetDelegate). Null: the body folds no constants.
        private RubyScope _declaringScope;

        // profiling feedback
        private int _calls;
        private readonly int[]/*!*/ _argKind;       // 0 = unseen, 1 = int, 2 = double, 3 = other/mixed, 4 = long
        // The CLR type of each argument, and of self, while profiling: one type, JitCompiler.MixedTypes
        // once two have differed, typeof(void) for nil. A hint the compiler keeps an Array or Hash
        // intrinsic off a receiver with (JitCompiler.ObservedType); it is never relied on for a result.
        private readonly Type[]/*!*/ _argClrType;
        private Type _selfClrType;
        private RubyClass _receiverClass;
        private bool _polymorphicReceiver;

        // ---- state -------------------------------------------------------------------------
        // Profiling:   the fast slot is empty; the trampoline profiles each call (Profile).
        // Specialized: the fast slot holds _current's entry.
        // Cooling:     _current was retired for deopting too often (OnDeopt); the fast slot holds
        //              the counted generic body (CoolingBody) until _current is tried again (CoolDownCheck).
        // Settled:     the fast slot holds the generic body for good (GiveUp).
        // Every change of state is made under _sync. A callback from an entry (EntryDeopted,
        // EntryStale) names its specialization and changes nothing unless that one is current, so
        // an older entry that another thread is still running cannot undo what replaced it.
        private const int Profiling = 0, Specialized = 1, Cooling = 2, Settled = 3;
        private volatile int _state;
        private Specialization _current;
        private readonly object/*!*/ _sync = new object();
        // 1 while a thread is in TrySpecialize. The threads that reach the threshold meanwhile -
        // they keep profiling, the fast slot being empty - run the generic body instead of
        // compiling the same method again (a race the base already had: one method specialized
        // 8 times over by 8 threads, and the last to finish installed its entry over whatever
        // had happened to the first one since).
        private int _compiling;

        protected JitStub(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule/*!*/ declaringModule, Delegate/*!*/ generic, int arity,
            JitKeywords keywords) {
            _ast = ast;
            _context = context;
            _declaringModule = declaringModule;
            _generic = generic;
            _arity = arity;
            _keywords = keywords;
            _positional = (keywords != null) ? arity - 1 : arity;
            _typedCount = (keywords != null) ? _positional + keywords.Count : arity;
            _argKind = new int[_typedCount];
            _argClrType = new Type[_typedCount];
        }

        protected abstract void Install(Delegate/*!*/ entry);
        internal abstract Delegate/*!*/ Trampoline { get; }
        // The stand-in for a retired entry: the generic body behind a countdown of _coolCalls.
        protected abstract Delegate/*!*/ CoolingBody { get; }

        /// <summary>
        /// One specialization of the method: its entries, and the deopt counts OnDeopt keeps for
        /// it. The entry hands it to EntryDeopted and EntryStale, so that a callback says which
        /// specialization it came from. Sealed, because the entry counts its calls through it,
        /// reaching it through the closure's constant array: a cast to a sealed class is a
        /// type-handle compare where one to JitStub is a helper call.
        /// </summary>
        internal sealed class Specialization {
            // While a deopt window is open (OnDeopt): the entries into the typed body still left
            // in it, counted down by an entry that can deopt (BuildEntry). Zero otherwise, and then
            // the entry only reads it, so threads calling a method that does not deopt write
            // nothing they share. Not interlocked: a lost decrement only blurs a rate.
            public int Countdown;
            internal static readonly System.Reflection.FieldInfo/*!*/ CountdownField = typeof(Specialization).GetField("Countdown");

            internal readonly JitStub/*!*/ Stub;
            internal Delegate Entry;
            internal Func<object, object, object, object, object, object, object> Direct;
            internal int WindowDeopts;
            // The least a deopt of the open window cost, throw to catch, in Stopwatch ticks; 0 if
            // none was timed (OnDeopt).
            internal long DeoptCost;
            // The constant version the typed bodies were compiled under (JitCode.UsesConstants).
            internal int ConstantVersion;

            internal Specialization(JitStub/*!*/ stub) {
                Stub = stub;
            }
        }

        /// <summary>Called by the trampoline on every call until the method is specialized.</summary>
        protected void Profile(object self, object[] args) {
            if (_state != Profiling) { return; }

            if (_keywords != null) {
                // what the prologue binds the keywords to, if it binds them at all
                object kw = args[_positional];
                if (_keywords.Matches(kw)) {
                    for (int j = 0; j < _keywords.Count; j++) {
                        // an omitted keyword whose default the typed body evaluates has no type to see
                        object v = _keywords.Value(kw, j);
                        if (v != RubyOps.DefaultArgument) { Observe(_positional + j, v); }
                    }
                }
            }
            int observed = _positional;
            if (_perCountMandatory >= 0) {
                // the omitted optional arguments are a suffix; only the passed ones have a type
                observed = _perCountMandatory;
                while (observed < _positional && args[observed] != RubyOps.DefaultArgument) { observed++; }
                _countsSeen |= 1 << (observed - _perCountMandatory);
            }
            for (int i = 0; i < observed; i++) {
                if (_hasDefault != null && _hasDefault[i] && args[i] == RubyOps.DefaultArgument) {
                    args[i] = _defaults[i];
                }
                Observe(i, args[i]);
            }

            RubyClass cls = JitRuntime.ClassOf(_context, self);
            if (_receiverClass == null) { _receiverClass = cls; } else if (_receiverClass != cls) { _polymorphicReceiver = true; }
            ObserveType(ref _selfClrType, self);

            if (++_calls < Threshold) { return; }
            _calls = 0;

            if (Interlocked.CompareExchange(ref _compiling, 1, 0) != 0) { return; }
            try {
                // A thread that read the empty fast slot before another one installed an entry
                // can get here after that one has let go of _compiling.
                if (_state != Profiling) { return; }
                if (!TrySpecialize()) {
                    GiveUp();
                }
            } catch (Exception) {
                GiveUp();
            } finally {
                Volatile.Write(ref _compiling, 0);
            }
        }

        private static void ObserveType(ref Type seen, object value) {
            Type t = (value == null) ? typeof(void) : value.GetType();
            if (seen == null) { seen = t; } else if (seen != t) { seen = JitCompiler.MixedTypes; }
        }

        private void Observe(int i, object arg) {
            ObserveType(ref _argClrType[i], arg);
            int k = (arg is int) ? 1 : (arg is double) ? 2 : (arg is long) ? 4 : 3;
            if (_argKind[i] == 0) {
                _argKind[i] = k;
            } else if (_argKind[i] != k) {
                // int and long are one Ruby class seen in two representations, so a
                // parameter that has been both is still an Integer: carry it as a long.
                bool bothIntegral = (k == 1 || k == 4) && (_argKind[i] == 1 || _argKind[i] == 4);
                _argKind[i] = bothIntegral ? 4 : 3;
            }
        }

        /// <summary>
        /// Stop profiling this method for good, and put the generic body back in the fast slot.
        /// Leaving the slot empty would be much worse than doing nothing: the trampoline would
        /// go on allocating an argument array and calling Profile - which returns immediately -
        /// on every call for the rest of the process.  A method the JIT declined is the common
        /// case, so that path has to cost one delegate hop and nothing else.
        /// </summary>
        private void GiveUp() {
            lock (_sync) {
                Settle();
            }
            // Every method that gets here passed JitScreen, so each one is either a genuinely
            // type-dependent refusal or the screen and JitCompiler.Emit having drifted apart.
            // Name it, so a count that creeps up can be traced to the construct responsible.
            JitRuntime.Rejected++;
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit] declined {0}/{1} arity {2} (passed the pre-screen)",
                    _declaringModule.Name ?? "main", _ast.Name, _arity);
            }
        }

        // Under _sync: the generic body, for good.
        private void Settle() {
            _state = Settled;
            _current = null;
            _direct = null;
            Install(_generic);
        }

        // How many times a method is specialized again after a method table change made its entry
        // stale. A program that keeps defining methods (a `def obj.x' per object, define_method in
        // a loop) would otherwise pay a compile every Threshold calls for as long as it runs.
        private const int MaxRespecializations = 8;
        private int _respecializations;

        /// <summary>
        /// Called by the entry when the global method version no longer matches its snapshot. That
        /// guard can never pass again, so leaving the entry installed would send every later call
        /// through the failed guard into the generic body: profile again and specialize under the
        /// new version instead, or settle on the generic body once that has happened too often.
        /// </summary>
        internal void VersionChanged(Specialization/*!*/ spec) {
            lock (_sync) {
                if (_state != Specialized || spec != _current) { return; }
                if (++_respecializations > MaxRespecializations) {
                    Settle();
                    return;
                }
                // deoptimize: drop the specialized entry and let profiling start over
                JitRuntime.Invalidations++;
                _current = null;
                _direct = null;
                _calls = 0;
                _state = Profiling;
                Install(null);
            }
        }

        // ---- deopt rate ----------------------------------------------------------------------
        // A deopt costs a CLR exception, about 10 us, on top of the generic call it falls back to,
        // so an entry that deopts on every call (arithmetic that always outgrows Int64, say) made
        // its method several times slower than not specializing it at all, for as long as the
        // program ran. A deopt now and then, though, is paid for by what the typed body saves on
        // the other calls, and how rare a deopt has to be for that depends on the body: deopting
        // on 1 call in K, a chain of Integer statements broke even with its generic body at K of
        // about 34 for 3 statements, 14 for 20, 8 for 40, 4 for 80 and 3 for 160. No one rate is
        // right for all of them; retiring at 1 call in 32, the first policy here, made a
        // 40-statement body 2x slower than the base, which never retires an entry.
        //
        // So the stub measures. The first deopt in a while opens a window of DeoptWindow entries
        // into the typed body, which the entry counts down (Countdown), and each deopt in it is
        // timed from the throw to the entry's catch. At the window's MaxDeopts'th deopt - n
        // deopts, the first one opening the window, over m entries - the entry is retired if
        //     (n - 1) * deopt > 1.25 * (m - (n - 1)) * generic
        // with deopt the least a deopt has taken in any window and generic the least median of the
        // generic calls Cool timed: while the entry was retired or, before it ever was, in a short
        // Probe once the generic body is compiled (the light compiler interprets its first calls
        // and compiles it in the background), repeated a few times as the runtime warms up. That
        // is the break-even of staying specialized as if the typed body cost nothing, with a
        // margin. A real one costs something, so the rule retires an entry only when its deopts
        // cost more than the whole generic body would on the calls that do not deopt: it keeps
        // some that lose a little (20 statements deopting on 1 call in 10-14), as the base keeps
        // them all, and does not retire one that pays its way. The least readings are the ones
        // to compare, because the two are timed at different moments, and either comes out up
        // to twice as high now and then - deopts took 10 us in most windows of a run and 16-23 us
        // in bursts of others, which retired entries that paid for themselves. A window that
        // runs out first just closes, and the entry counts nothing until the next deopt: a method
        // that does not deopt writes nothing on its calls that the threads calling it share.
        //
        // A retired entry is tried again later, because the deopts may come from one phase of the
        // input - large values while a program sets up, small ones for the millions of calls after
        // that - and retiring it for good then made a 10-statement method 3x slower for the rest of
        // the process. The method keeps its compiled entry and runs the generic body until both
        // CoolCheckCalls generic calls and a cool-down have passed; the cool-down starts at
        // CoolDownMs and doubles at each retirement. A retry that fails again costs one more window
        // of deopts, about 0.2 ms, against a cool-down of at least 8 ms; after MaxRetirements the
        // method keeps its generic body for good.
        internal const int MaxDeopts = 16;
        private const int DeoptWindow = MaxDeopts * 64;
        private const int MaxRetirements = 8;
        private const int CoolDownMs = 8;
        private const int CoolCheckCalls = 1024;
        private const int CoolSampleMask = 7;    // Cool times one generic call in 8, at random
        private const int ProbeCalls = 2 * MaxDeopts;  // and every one in a probe
        private const int MaxProbes = 6;
        private const int ProbeIntervalMs = 100;
        private const int MaxProbeDelays = 8;

        private int _retirements;
        private long _coolUntil;          // Stopwatch timestamp: the end of the cool-down
        protected int _coolCalls;         // generic calls until CoolDownCheck looks at the time
        // The latest generic calls Cool timed (a ring of MaxDeopts), which ones it times, and the
        // least median of them taken when the entry was put back; in Stopwatch ticks.
        private long[] _coolSamples;
        private int _coolSampleCount;
        private int _coolMask;
        private uint _coolRandom = 0x9E3779B9;
        private long _genericCost;
        private long _deoptCost;          // the least a deopt took in any window, in Stopwatch ticks
        private int _probes;
        private long _probeAfter;         // Stopwatch timestamp: no probe before it
        // Set once the light compiler has compiled the generic body (Wrap), or from the start if it was
        // never interpreted. A probe waits for it, for at most MaxProbeDelays windows.
        private volatile bool _genericCompiled;
        private int _probeDelays;

        private static MSA.Expression/*!*/ CountEntry(Specialization/*!*/ spec) {
            var countdown = Ast.Field(Ast.Constant(spec), Specialization.CountdownField);
            return Ast.IfThen(Ast.GreaterThan(countdown, Ast.Constant(0)), Ast.PreDecrementAssign(countdown));
        }

        /// <summary>
        /// Called by an entry (and the direct entry) when the typed body deopted and the call is
        /// being re-run generically, with what the exception cost (-1 if unknown): retires the
        /// entry once its deopts cost more than its typed body saves (see MaxDeopts).
        /// </summary>
        internal void OnDeopt(Specialization/*!*/ spec, long deoptCost) {
            if (_state != Specialized || spec != _current) { return; }
            if (spec.Countdown <= 0) {
                // the first deopt in a while: open a window
                spec.WindowDeopts = 1;
                spec.DeoptCost = (deoptCost > 0) ? deoptCost : 0;
                spec.Countdown = DeoptWindow;
                return;
            }
            // Not interlocked, like the rest of a window: a deopt racing with another on a second
            // thread can lose its count or its cost, which only blurs a rate.
            if (deoptCost > 0 && (spec.DeoptCost == 0 || deoptCost < spec.DeoptCost)) { spec.DeoptCost = deoptCost; }
            if (++spec.WindowDeopts < MaxDeopts) { return; }

            int entries = DeoptWindow - spec.Countdown;
            int deopts = spec.WindowDeopts - 1;
            if (spec.DeoptCost > 0 && (_deoptCost == 0 || spec.DeoptCost < _deoptCost)) { _deoptCost = spec.DeoptCost; }
            long deopt = _deoptCost;
            long generic = _genericCost;
            // close the window; the next deopt opens another
            spec.Countdown = 0;
            if (deopt <= 0) { return; }
            if (generic <= 0 || 4 * deopts * deopt <= 5 * (entries - deopts) * generic) {
                if (generic > 0 && JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] kept {0}/{1} arity {2}: {3} deopts in {4} calls ({5})",
                        _declaringModule.Name ?? "main", _ast.Name, _arity, spec.WindowDeopts, entries, Costs(deopt, generic));
                }
                // Time the generic body: it has not been yet, or it may have got faster since - the
                // runtime methods it calls start out in the CLR's quick first-tier code, which is
                // replaced some 100 ms on.
                if (_probes < MaxProbes && Stopwatch.GetTimestamp() >= _probeAfter && (_genericCompiled || ++_probeDelays > MaxProbeDelays)) {
                    Probe(spec, entries, deopt);
                }
                return;
            }

            lock (_sync) {
                if (_state != Specialized || spec != _current) { return; }
                JitRuntime.DeoptRetired++;
                bool final = ++_retirements > MaxRetirements;
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] retired {0}/{1} arity {2}: {3} deopts in {4} calls ({5}){6}",
                        _declaringModule.Name ?? "main", _ast.Name, _arity, spec.WindowDeopts, entries, Costs(deopt, generic),
                        final ? ", for good" : String.Format(", retry in {0} ms", CoolDownMs << (_retirements - 1)));
                }
                if (final) {
                    Settle();
                    return;
                }
                StartCooling(Stopwatch.GetTimestamp() + (Stopwatch.Frequency * CoolDownMs / 1000) * (1L << (_retirements - 1)),
                    CoolCheckCalls, CoolSampleMask);
            }
        }

        /// <summary>
        /// Time the generic body: run it for ProbeCalls calls, timing each, then put the entry back.
        /// A probe costs what the typed body would have saved on those calls; there are at most
        /// MaxProbes, ProbeIntervalMs apart and then twice as far each time.
        /// </summary>
        private void Probe(Specialization/*!*/ spec, int entries, long deopt) {
            lock (_sync) {
                if (_state != Specialized || spec != _current || _probes >= MaxProbes) { return; }
                _probeAfter = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * ProbeIntervalMs / 1000) * (1L << _probes);
                _probes++;
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] timing the generic body of {0}/{1} arity {2}: {3} deopts in {4} calls (deopt {5:F1} us){6}",
                        _declaringModule.Name ?? "main", _ast.Name, _arity, spec.WindowDeopts, entries, deopt * 1e6 / Stopwatch.Frequency,
                        _genericCompiled ? "" : ", not known to be compiled");
                }
                StartCooling(0, ProbeCalls, 0);
            }
        }

        // Under _sync: the generic body until `until' and `calls' generic calls have passed, timing
        // the calls Cool picks with `mask'.
        private void StartCooling(long until, int calls, int mask) {
            _direct = null;
            _coolUntil = until;
            _coolCalls = calls;
            _coolMask = mask;
            _coolSamples ??= new long[MaxDeopts];
            _coolSampleCount = 0;
            _state = Cooling;
            Install(CoolingBody);
        }

        private static long Median(long[]/*!*/ costs, int count) {
            var sorted = new long[count];
            Array.Copy(costs, sorted, count);
            Array.Sort(sorted);
            return sorted[count / 2];
        }

        private static string/*!*/ Costs(long deopt, long generic) {
            return String.Format("deopt {0:F1} us, generic {1:F2} us",
                deopt * 1e6 / Stopwatch.Frequency, generic * 1e6 / Stopwatch.Frequency);
        }

        /// <summary>
        /// Whether the cooling stand-in is to time this call: one in CoolSampleMask + 1, picked at
        /// random so that the calls timed are not in step with a pattern of the program's own.
        /// </summary>
        protected bool CoolPick() {
            uint r = _coolRandom;
            r ^= r << 13; r ^= r >> 17; r ^= r << 5;
            _coolRandom = r;
            return (r & _coolMask) == 0;
        }

        /// <summary>Called by the cooling stand-in with the start of a generic call it timed.</summary>
        protected void CoolSample(long start) {
            long cost = Stopwatch.GetTimestamp() - start;
            var samples = _coolSamples;
            int i = _coolSampleCount;
            if (samples != null) {
                samples[i & (MaxDeopts - 1)] = cost;
                _coolSampleCount = i + 1;
            }
        }

        /// <summary>
        /// Called by the cooling stand-in every CoolCheckCalls calls (ProbeCalls in a probe): puts
        /// the entry back once its cool-down is over.
        /// </summary>
        protected void CoolDownCheck() {
            lock (_sync) {
                if (_state != Cooling) { return; }
                _coolCalls = CoolCheckCalls;
                if (Stopwatch.GetTimestamp() < _coolUntil) { return; }

                if (_coolSampleCount > 0) {
                    long median = Median(_coolSamples, Math.Min(_coolSampleCount, MaxDeopts));
                    if (_genericCost == 0 || median < _genericCost) { _genericCost = median; }
                }
                var spec = _current;
                spec.Countdown = 0;
                spec.WindowDeopts = 0;
                JitRuntime.DeoptRetried++;
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] retrying {0}/{1} arity {2} (generic {3:F2} us)", _declaringModule.Name ?? "main", _ast.Name, _arity,
                        _genericCost * 1e6 / Stopwatch.Frequency);
                }
                _state = Specialized;
                _direct = spec.Direct;
                Install(spec.Entry);
            }
        }

        private bool TrySpecialize() {
            if (JitRuntime.Disabled || _context.HasRefinements) { return false; }
            if (_perCountMandatory >= 0) { return TrySpecializePerCount(); }

            var types = ProfiledTypes(_typedCount);

            // The method version the entry guards on is read before compiling, so that it is never
            // newer than what the compiler resolved against - each built-in operator it checks, and
            // the self-call check below. Read after, a method redefined (on another thread) while a
            // large body compiled would be inside the snapshot, and the entry would go on running
            // the operator it replaced.
            int version = RubyModule.GlobalMethodVersion;
            int constantVersion = _context.ConstantAccessVersion;

            long start = Stopwatch.GetTimestamp();
            var code = JitCompiler.TryCompile(_ast, _context, types, _keywords, -1, _declaringScope, Hints());
            if (code == null) { return false; }

            bool needsClassGuard = code.EmittedSelfCall;
            if (needsClassGuard) {
                if (_receiverClass == null || _polymorphicReceiver) { return false; }
                if (!SelfCallResolvesHere(_receiverClass)) { return false; }
            }

            // The typed body only has to exist as a delegate of its own when it calls itself: the
            // direct self-call goes through SelfCell. Otherwise the entry invokes the lambda
            // itself, which the expression compiler inlines, so a specialized call is the
            // trampoline hop and the entry and nothing more.
            MSA.Expression callee;
            if (code.EmittedSelfCall) {
                Delegate typed = code.Lambda.Compile();
                code.SelfCell.Value = typed;
                callee = Ast.Constant(typed, typed.GetType());
            } else {
                callee = code.Lambda;
            }

            var spec = new Specialization(this);
            spec.ConstantVersion = constantVersion;
            spec.Entry = BuildEntry(code, callee, version, needsClassGuard ? _receiverClass : null, spec).Compile();
            if (_keywords != null && !needsClassGuard && _positional <= JitKeywordCall.MaxPositional &&
                _keywords.Count <= JitKeywordCall.MaxKeywords) {
                spec.Direct = BuildDirectEntry(code, callee, version, spec).Compile();
            }

            lock (_sync) {
                // Nothing but this thread, which holds _compiling, takes the stub out of Profiling;
                // the test is only there to keep it that way.
                if (_state != Profiling) { return true; }
                _current = spec;
                _state = Specialized;
                Install(spec.Entry);
                _direct = spec.Direct;
            }

            JitRuntime.CompileTicks += Stopwatch.GetTimestamp() - start;
            JitRuntime.Specialized++;
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit] specialized {0}/{1} arity {2} -> {3}{4}",
                    _declaringModule.Name ?? "main", _ast.Name, _arity, code.ReturnType, needsClassGuard ? " (self-call)" : "");
            }
            return true;
        }

        private JitCompiler.ReceiverHints/*!*/ Hints() {
            return new JitCompiler.ReceiverHints { Parameters = (Type[])_argClrType.Clone(), Self = _selfClrType };
        }

        private JT[]/*!*/ ProfiledTypes(int count) {
            var types = new JT[count];
            for (int i = 0; i < count; i++) {
                if (_storedOnly != null && _storedOnly[i]) { types[i] = JT.Obj; continue; }
                switch (_argKind[i]) {
                    case 1: types[i] = JT.Int; break;
                    case 2: types[i] = JT.Dbl; break;
                    case 4: types[i] = JT.Lng; break;
                    // never passed: the typed body types the keyword by its default
                    case 0: types[i] = PassedBoxed(i) ? JT.None : JT.Obj; break;
                    default: types[i] = JT.Obj; break;
                }
            }
            return types;
        }

        /// <summary>
        /// A keyword whose default the typed body evaluates (JitKeywords.DefaultExprs): the body takes the
        /// boxed value, or RubyOps.DefaultArgument, which the entry's type guard lets through.
        /// </summary>
        private bool PassedBoxed(int i) {
            return _keywords != null && i >= _positional && _keywords.HasDefaultExpr(i - _positional);
        }

        private MSA.Expression/*!*/ AllowDefault(int i, MSA.Expression/*!*/ value, MSA.Expression/*!*/ guard) {
            return PassedBoxed(i)
                ? Ast.OrElse(Ast.ReferenceEqual(value, Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"))), guard)
                : guard;
        }

        /// <summary>
        /// One typed body for each number of optional arguments the calls so far have passed; a call
        /// passing a number no body was compiled for runs the generic body.
        /// </summary>
        private bool TrySpecializePerCount() {
            // read before compiling, as in TrySpecialize
            int version = RubyModule.GlobalMethodVersion;
            int constantVersion = _context.ConstantAccessVersion;

            long start = Stopwatch.GetTimestamp();
            int optionalCount = _positional - _perCountMandatory;
            var codes = new JitCode[optionalCount + 1];
            var callees = new MSA.Expression[optionalCount + 1];
            bool any = false, pinned = false;
            for (int k = 0; k <= optionalCount; k++) {
                if ((_countsSeen & (1 << k)) == 0) { continue; }
                var code = JitCompiler.TryCompile(_ast, _context, ProfiledTypes(_perCountMandatory + k), null, k, _declaringScope, Hints());
                if (code == null) { continue; }
                if (code.EmittedSelfCall) {
                    if (_receiverClass == null || _polymorphicReceiver || !SelfCallResolvesHere(_receiverClass)) { continue; }
                    pinned = true;
                }
                codes[k] = code;
                any = true;
            }
            if (!any) { return false; }

            for (int k = 0; k <= optionalCount; k++) {
                var code = codes[k];
                if (code == null) { continue; }
                // as in TrySpecialize: a body of its own only when it calls itself
                if (code.EmittedSelfCall) {
                    Delegate typed = code.Lambda.Compile();
                    code.SelfCell.Value = typed;
                    callees[k] = Ast.Constant(typed, typed.GetType());
                } else {
                    callees[k] = code.Lambda;
                }
            }

            // one Specialization for all the bodies: they retire, cool down and go stale together
            var spec = new Specialization(this);
            spec.ConstantVersion = constantVersion;
            spec.Entry = BuildPerCountEntry(codes, callees, version, pinned ? _receiverClass : null, spec).Compile();

            lock (_sync) {
                // as in TrySpecialize
                if (_state != Profiling) { return true; }
                _current = spec;
                _state = Specialized;
                Install(spec.Entry);
            }

            JitRuntime.CompileTicks += Stopwatch.GetTimestamp() - start;
            JitRuntime.Specialized++;
            if (JitRuntime.Verbose) {
                for (int k = 0; k <= optionalCount; k++) {
                    if (codes[k] == null) { continue; }
                    Console.Error.WriteLine("[jit] specialized {0}/{1} arity {2} passing {3} optional -> {4}{5}",
                        _declaringModule.Name ?? "main", _ast.Name, _arity, k, codes[k].ReturnType, codes[k].EmittedSelfCall ? " (self-call)" : "");
                }
            }
            return true;
        }

        /// <summary>
        /// The direct self-call is only correct if the name still resolves, from the profiled
        /// receiver class, to this very body.
        /// </summary>
        private bool SelfCallResolvesHere(RubyClass/*!*/ cls) {
            var resolved = cls.ResolveMethod(_ast.Name, VisibilityContext.AllVisible);
            if (!resolved.Found) { return false; }
            var info = resolved.Info as RubyMethodInfo;
            return info != null && ReferenceEquals(info.GetSyntaxTree(), _ast);
        }

        // ---- entry lambda ------------------------------------------------------------------

        private MSA.LambdaExpression/*!*/ BuildEntry(JitCode/*!*/ code, MSA.Expression/*!*/ callee, int version, RubyClass pinnedClass,
            Specialization/*!*/ spec) {
            var self = Ast.Parameter(typeof(object), "self");
            var blk = Ast.Parameter(typeof(Proc), "block");
            var args = new MSA.ParameterExpression[_arity];
            for (int i = 0; i < _arity; i++) {
                args[i] = Ast.Parameter(typeof(object), "a" + i);
            }

            // The values the typed parameters are made of: the positional arguments - an omitted
            // optional one being its default - then, for a method with keywords, what the prologue
            // would bind each keyword to. The generic body is still handed the arguments as they came.
            var values = new MSA.Expression[_typedCount];
            var defaulted = new List<MSA.ParameterExpression>();
            var fillDefaults = new List<MSA.Expression>();
            for (int i = 0; i < _positional; i++) {
                if (_hasDefault != null && _hasDefault[i]) {
                    var v = Ast.Variable(typeof(object), "v" + i);
                    values[i] = v;
                    defaulted.Add(v);
                    fillDefaults.Add(Ast.Assign(v, Ast.Condition(
                        Ast.ReferenceEqual(args[i], Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"))),
                        Ast.Constant(_defaults[i], typeof(object)),
                        args[i]
                    )));
                } else {
                    values[i] = args[i];
                }
            }

            MSA.Expression test = null;
            if (pinnedClass != null) {
                test = And(test, Ast.ReferenceEqual(
                    Ast.Call(JitRuntime.M("ClassOf"), Ast.Constant(_context), self),
                    Ast.Constant(pinnedClass, typeof(RubyClass))));
            }

            MSA.ParameterExpression[] keywordValues = null;
            if (_keywords != null) {
                MSA.Expression kw = args[_positional];
                // the separation check on the last positional: `m("a" => 1)' gave it the keywords
                if (_positional > 0 && code.ParameterTypes[_positional - 1] == JT.Obj) {
                    test = And(test, Ast.Not(Ast.Call(typeof(RubyOps).GetMethod("IsKeywordArgumentsHash"), args[_positional - 1])));
                }
                keywordValues = new MSA.ParameterExpression[_keywords.Count];
                for (int j = 0; j < keywordValues.Length; j++) {
                    values[_positional + j] = keywordValues[j] = Ast.Variable(typeof(object), "kw" + j);
                }
                var info = Ast.Constant(_keywords, typeof(object));
                if (keywordValues.Length <= 3) {
                    // match and bind in one call, the values coming back through out parameters
                    var readArgs = new MSA.Expression[keywordValues.Length + 2];
                    readArgs[0] = info;
                    readArgs[1] = kw;
                    Array.Copy(keywordValues, 0, readArgs, 2, keywordValues.Length);
                    test = And(test, Ast.Call(JitRuntime.M("KeywordsRead" + keywordValues.Length), readArgs));
                } else {
                    test = And(test, Ast.Call(JitRuntime.M("KeywordsMatch"), info, kw));
                    for (int j = 0; j < keywordValues.Length; j++) {
                        test = And(test, Ast.Block(
                            Ast.Assign(keywordValues[j], Ast.Call(JitRuntime.M("KeywordValue"), info, kw, Ast.Constant(j))),
                            Ast.Constant(true)));
                    }
                }
            }

            for (int i = 0; i < _typedCount; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int: test = And(test, AllowDefault(i, values[i], Ast.TypeIs(values[i], typeof(int)))); break;
                    case JT.Dbl: test = And(test, AllowDefault(i, values[i], Ast.TypeIs(values[i], typeof(double)))); break;
                    // An Integer parameter carried as a long accepts either representation: a
                    // value in Int32 range arrives as an Int32 and only a larger one as an Int64.
                    case JT.Lng: test = And(test, AllowDefault(i, values[i], Ast.Call(JitRuntime.M("IsIntegral"), values[i]))); break;
                }
            }

            var typedArgs = new MSA.Expression[_typedCount + 1];
            typedArgs[0] = self;
            for (int i = 0; i < _typedCount; i++) {
                if (PassedBoxed(i)) { typedArgs[i + 1] = values[i]; continue; }
                switch (code.ParameterTypes[i]) {
                    case JT.Int: typedArgs[i + 1] = Ast.Unbox(values[i], typeof(int)); break;
                    case JT.Dbl: typedArgs[i + 1] = Ast.Unbox(values[i], typeof(double)); break;
                    case JT.Lng: typedArgs[i + 1] = Ast.Call(JitRuntime.M("ToLong"), values[i]); break;
                    default: typedArgs[i + 1] = values[i]; break;
                }
            }

            MSA.Expression fast = Ast.Invoke(callee, typedArgs);
            if (code.ReturnType == JT.Lng) {
                // The one place a specialized method's value becomes a Ruby object: it leaves
                // through the representation funnel, so a result that fits in an Int32 is one.
                fast = Ast.Call(JitRuntime.M("NarrowLong"), fast);
            } else if (fast.Type != typeof(object)) {
                fast = Ast.Convert(fast, typeof(object));
            }

            if (code.CanDeopt) {
                // The generic body runs after the handler, not inside it: an exception it raises
                // from within a catch block would leave the frames outside this one out of its
                // backtrace. While a deopt window is open the entry counts its calls of the typed
                // body (see OnDeopt).
                var result = Ast.Variable(typeof(object), "#result");
                var deopted = Ast.Constant(Deopted, typeof(object));
                fast = Ast.Block(typeof(object), new[] { result },
                    CountEntry(spec),
                    WithUndoLog(code, handler => Ast.TryCatch(
                        Ast.Assign(result, fast),
                        Ast.Catch(typeof(JitDeoptException), Ast.Block(
                            handler,
                            Ast.Call(JitRuntime.M("EntryDeopted"), Ast.Constant(spec, typeof(object))),
                            Ast.Assign(result, deopted)
                        ))
                    )),
                    Ast.Condition(Ast.ReferenceEqual(result, deopted), GenericCall(self, blk, args), result, typeof(object))
                );
            }

            MSA.Expression body = (test != null) ? Ast.Condition(test, fast, GenericCall(self, blk, args), typeof(object)) : fast;
            if (keywordValues != null) {
                body = Ast.Block(typeof(object), keywordValues, body);
            }
            if (defaulted.Count > 0) {
                fillDefaults.Add(body);
                body = Ast.Block(typeof(object), defaulted, fillDefaults);
            }

            // A stale version fails this guard on every later call; the stub takes the entry down
            // (JitStub.VersionChanged) and this one call runs the generic body.
            body = Ast.Condition(
                VersionTest(version, spec, code.UsesConstants),
                body,
                Ast.Block(
                    Ast.Call(JitRuntime.M("EntryStale"), Ast.Constant(spec, typeof(object))),
                    GenericCall(self, blk, args)
                ),
                typeof(object)
            );

            var all = new MSA.ParameterExpression[_arity + 2];
            all[0] = self;
            all[1] = blk;
            Array.Copy(args, 0, all, 2, _arity);
            return Ast.Lambda(_generic.GetType(), body, EntryNamePrefix + _ast.Name, all);
        }

        /// <summary>
        /// The entry JitKeywordCall calls with the keyword values already bound (and the defaults of the
        /// ones the call left out filled in): the same guards as BuildEntry's, but a failed one - or a
        /// deopt - answers NoDirect, and the caller then makes the call the ordinary way.
        /// </summary>
        private MSA.Expression<Func<object, object, object, object, object, object, object>>/*!*/ BuildDirectEntry(JitCode/*!*/ code,
            MSA.Expression/*!*/ callee, int version, Specialization/*!*/ spec) {

            var self = Ast.Parameter(typeof(object), "self");
            var slots = new MSA.ParameterExpression[JitKeywordCall.MaxPositional + JitKeywordCall.MaxKeywords];
            for (int i = 0; i < slots.Length; i++) {
                slots[i] = Ast.Parameter(typeof(object), "s" + i);
            }
            var values = new MSA.Expression[_typedCount];
            for (int i = 0; i < _positional; i++) {
                values[i] = slots[i];
            }
            for (int j = 0; j < _keywords.Count; j++) {
                values[_positional + j] = slots[JitKeywordCall.MaxPositional + j];
            }

            MSA.Expression test = VersionTest(version, spec, code.UsesConstants);
            // the separation check on the last positional, as in BuildEntry
            if (_positional > 0 && code.ParameterTypes[_positional - 1] == JT.Obj) {
                test = And(test, Ast.Not(Ast.Call(typeof(RubyOps).GetMethod("IsKeywordArgumentsHash"), values[_positional - 1])));
            }
            var typedArgs = new MSA.Expression[_typedCount + 1];
            typedArgs[0] = self;
            for (int i = 0; i < _typedCount; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int:
                        test = And(test, AllowDefault(i, values[i], Ast.TypeIs(values[i], typeof(int))));
                        typedArgs[i + 1] = Ast.Unbox(values[i], typeof(int));
                        break;
                    case JT.Dbl:
                        test = And(test, AllowDefault(i, values[i], Ast.TypeIs(values[i], typeof(double))));
                        typedArgs[i + 1] = Ast.Unbox(values[i], typeof(double));
                        break;
                    case JT.Lng:
                        test = And(test, AllowDefault(i, values[i], Ast.Call(JitRuntime.M("IsIntegral"), values[i])));
                        typedArgs[i + 1] = Ast.Call(JitRuntime.M("ToLong"), values[i]);
                        break;
                    default:
                        typedArgs[i + 1] = values[i];
                        break;
                }
                if (PassedBoxed(i)) { typedArgs[i + 1] = values[i]; }
            }

            MSA.Expression fast = Ast.Invoke(callee, typedArgs);
            if (code.ReturnType == JT.Lng) {
                fast = Ast.Call(JitRuntime.M("NarrowLong"), fast);
            } else if (fast.Type != typeof(object)) {
                fast = Ast.Convert(fast, typeof(object));
            }
            var noDirect = Ast.Field(null, typeof(JitKeywordCall).GetField("NoDirect"));
            if (code.CanDeopt) {
                fast = Ast.Block(
                    CountEntry(spec),
                    fast
                );
                var direct = fast;
                fast = WithUndoLog(code, handler => Ast.TryCatch(direct, Ast.Catch(typeof(JitDeoptException), Ast.Block(
                    handler,
                    Ast.Call(JitRuntime.M("EntryDeopted"), Ast.Constant(spec, typeof(object))),
                    noDirect
                ))));
            }
            var body = Ast.Condition(test, fast, noDirect, typeof(object));

            var all = new MSA.ParameterExpression[slots.Length + 1];
            all[0] = self;
            Array.Copy(slots, 0, all, 1, slots.Length);
            return Ast.Lambda<Func<object, object, object, object, object, object, object>>(body, DirectEntryNamePrefix + _ast.Name, all);
        }

        /// <summary>
        /// BuildEntry for per-count bodies: which arguments are RubyOps.DefaultArgument picks the body
        /// (a call leaves out a suffix of the optional parameters), then the same guards as BuildEntry's
        /// over the arguments that body takes.
        /// </summary>
        private MSA.LambdaExpression/*!*/ BuildPerCountEntry(JitCode[]/*!*/ codes, MSA.Expression[]/*!*/ callees, int version, RubyClass pinnedClass,
            Specialization/*!*/ spec) {
            var self = Ast.Parameter(typeof(object), "self");
            var blk = Ast.Parameter(typeof(Proc), "block");
            var args = new MSA.ParameterExpression[_arity];
            for (int i = 0; i < _arity; i++) {
                args[i] = Ast.Parameter(typeof(object), "a" + i);
            }

            MSA.Expression body = null;
            for (int k = codes.Length - 1; k >= 0; k--) {
                MSA.Expression arm = (codes[k] != null)
                    ? BuildPerCountArm(codes[k], callees[k], self, blk, args, _perCountMandatory + k, pinnedClass, spec)
                    : GenericCall(self, blk, args);
                body = (body == null) ? arm : Ast.Condition(
                    Ast.ReferenceEqual(args[_perCountMandatory + k], Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"))),
                    arm, body, typeof(object));
            }

            bool usesConstants = false;
            foreach (var code in codes) {
                if (code != null && code.UsesConstants) { usesConstants = true; }
            }
            body = Ast.Condition(
                VersionTest(version, spec, usesConstants),
                body,
                Ast.Block(
                    Ast.Call(JitRuntime.M("EntryStale"), Ast.Constant(spec, typeof(object))),
                    GenericCall(self, blk, args)
                ),
                typeof(object)
            );

            var all = new MSA.ParameterExpression[_arity + 2];
            all[0] = self;
            all[1] = blk;
            Array.Copy(args, 0, all, 2, _arity);
            return Ast.Lambda(_generic.GetType(), body, EntryNamePrefix + _ast.Name, all);
        }

        private MSA.Expression/*!*/ BuildPerCountArm(JitCode/*!*/ code, MSA.Expression/*!*/ callee, MSA.ParameterExpression/*!*/ self,
            MSA.ParameterExpression/*!*/ blk, MSA.ParameterExpression[]/*!*/ args, int count, RubyClass pinnedClass,
            Specialization/*!*/ spec) {

            MSA.Expression test = null;
            if (pinnedClass != null && code.EmittedSelfCall) {
                test = And(test, Ast.ReferenceEqual(
                    Ast.Call(JitRuntime.M("ClassOf"), Ast.Constant(_context), self),
                    Ast.Constant(pinnedClass, typeof(RubyClass))));
            }
            var typedArgs = new MSA.Expression[count + 1];
            typedArgs[0] = self;
            for (int i = 0; i < count; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int:
                        test = And(test, Ast.TypeIs(args[i], typeof(int)));
                        typedArgs[i + 1] = Ast.Unbox(args[i], typeof(int));
                        break;
                    case JT.Dbl:
                        test = And(test, Ast.TypeIs(args[i], typeof(double)));
                        typedArgs[i + 1] = Ast.Unbox(args[i], typeof(double));
                        break;
                    case JT.Lng:
                        test = And(test, Ast.Call(JitRuntime.M("IsIntegral"), args[i]));
                        typedArgs[i + 1] = Ast.Call(JitRuntime.M("ToLong"), args[i]);
                        break;
                    default:
                        typedArgs[i + 1] = args[i];
                        break;
                }
            }

            MSA.Expression fast = Ast.Invoke(callee, typedArgs);
            if (code.ReturnType == JT.Lng) {
                fast = Ast.Call(JitRuntime.M("NarrowLong"), fast);
            } else if (fast.Type != typeof(object)) {
                fast = Ast.Convert(fast, typeof(object));
            }

            if (code.CanDeopt) {
                // as in BuildEntry: the generic body runs after the handler, not inside it, and the
                // deopts count towards retiring the entry (OnDeopt)
                var result = Ast.Variable(typeof(object), "#result");
                var deopted = Ast.Constant(Deopted, typeof(object));
                fast = Ast.Block(typeof(object), new[] { result },
                    CountEntry(spec),
                    WithUndoLog(code, handler => Ast.TryCatch(
                        Ast.Assign(result, fast),
                        Ast.Catch(typeof(JitDeoptException), Ast.Block(
                            handler,
                            Ast.Call(JitRuntime.M("EntryDeopted"), Ast.Constant(spec, typeof(object))),
                            Ast.Assign(result, deopted)
                        ))
                    )),
                    Ast.Condition(Ast.ReferenceEqual(result, deopted), GenericCall(self, blk, args), result, typeof(object))
                );
            }
            return (test != null) ? Ast.Condition(test, fast, GenericCall(self, blk, args), typeof(object)) : fast;
        }

        /// <summary>The entry guard: the method version, and the constant version if the body folded constants.</summary>
        private MSA.Expression/*!*/ VersionTest(int version, Specialization/*!*/ spec, bool usesConstants) {
            MSA.Expression test = Ast.Call(JitRuntime.M("VersionOk"), Ast.Constant(version));
            if (usesConstants) {
                test = Ast.AndAlso(test, Ast.Call(JitRuntime.M("ConstantsOk"), Ast.Constant(_context), Ast.Constant(spec.ConstantVersion)));
            }
            return test;
        }

        /// <summary>
        /// The typed body's call, made by <paramref name="call"/> around the statement its deopt handler
        /// is to start with: for a body that writes Hash or Array elements (JitCode.UsesUndoLog), the
        /// handler takes those writes back (JitUndoLog) before the generic body runs the call again, and
        /// however the call ends, the writes it made and kept stand. Otherwise the handler does nothing.
        /// </summary>
        private static MSA.Expression/*!*/ WithUndoLog(JitCode/*!*/ code, Func<MSA.Expression, MSA.Expression>/*!*/ call) {
            if (!code.UsesUndoLog) {
                return call(Ast.Empty());
            }
            var mark = Ast.Variable(typeof(int), "#undo-mark");
            var guarded = call(Ast.Call(JitRuntime.M("UndoRollback"), mark));
            return Ast.Block(guarded.Type, new[] { mark },
                Ast.Assign(mark, Ast.Call(JitRuntime.M("UndoMark"))),
                Ast.TryFinally(guarded, Ast.Call(JitRuntime.M("UndoRelease"), mark))
            );
        }

        private static MSA.Expression/*!*/ And(MSA.Expression left, MSA.Expression/*!*/ right) {
            return (left != null) ? Ast.AndAlso(left, right) : right;
        }

        private MSA.Expression/*!*/ GenericCall(MSA.ParameterExpression/*!*/ self, MSA.ParameterExpression/*!*/ blk, MSA.ParameterExpression[]/*!*/ args) {
            var callArgs = new MSA.Expression[_arity + 2];
            callArgs[0] = self;
            callArgs[1] = blk;
            Array.Copy(args, 0, callArgs, 2, _arity);
            return Ast.Invoke(Ast.Constant(_generic, _generic.GetType()), callArgs);
        }

        // ---- factory -----------------------------------------------------------------------

        /// <summary>
        /// Decides, statically and cheaply, whether a method is worth putting a stub on, and
        /// returns the trampoline that replaces the generic delegate. Returns the generic
        /// delegate unchanged when it is not.
        /// </summary>
        internal static Delegate/*!*/ Wrap(Delegate/*!*/ generic, MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule/*!*/ declaringModule) {
            return Wrap(generic, ast, context, declaringModule, null);
        }

        internal static Delegate/*!*/ Wrap(Delegate/*!*/ generic, MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule/*!*/ declaringModule,
            RubyScope declaringScope) {
            if (JitRuntime.Disabled) { return generic; }
            JitRuntime.HookStats();

            var ps = ast.Parameters;
            if (ps.Unsplat != null || ps.Block != null || ast.UsesBlock) { return generic; }
            int mandatoryCount = ps.Mandatory.Length;
            int arity = mandatoryCount + ps.Optional.Length;
            if (arity > MaxArity) { return generic; }
            for (int i = 0; i < mandatoryCount; i++) {
                if (!(ps.Mandatory[i] is LocalVariable)) { return generic; }
            }

            // An optional parameter is taken when its default is a literal the typed body could
            // have produced itself - nil, true, false, a Fixnum or a Float: evaluating it has no
            // effect and cannot see the other parameters, so the entry may fill it in up front.
            // Keyword parameters arrive in one trailing optional parameter (see JitKeywords); a method
            // with keywords has no other optional one (PrismAstBridge lowers that mix to a splat).
            // Any other default makes it a method with per-count bodies (TrySpecializePerCount), which
            // evaluate the defaults the call left out themselves; JitScreen checks that they can.
            JitKeywords keywords = null;
            bool[] hasDefault = null;
            object[] defaults = null;
            bool perCount = false;
            if (ps.Optional.Length > 0 && (keywords = JitKeywords.TryRecognize(ast, context)) == null) {
                hasDefault = new bool[arity];
                defaults = new object[arity];
                for (int i = 0; i < ps.Optional.Length; i++) {
                    var opt = ps.Optional[i];
                    if (!(opt.Left is LocalVariable)) { return generic; }
                    var literal = opt.Right as Literal;
                    object v = (literal != null) ? literal.Value : null;
                    if (literal == null || !(v == null || v is bool || v is int || v is double)) {
                        perCount = true;
                        continue;
                    }
                    hasDefault[mandatoryCount + i] = true;
                    defaults[mandatoryCount + i] = v;
                }
                if (perCount) {
                    hasDefault = null;
                    defaults = null;
                }
            }
            // Tracing and coverage need the real frame.
            if (context.RubyOptions.EnableTracing || context.RubyOptions.Profile) { return generic; }

            // A body the compiler can never accept, whatever types it is called with, is not
            // wrapped at all. Once wrapped, the trampoline is what the runtime caches and bakes
            // into call site rules, so a method declined later still pays one extra indirect
            // call on every call for the life of the process.
            string rejectedBy;
            HashSet<string> otherUses;
            if (!JitScreen.CanEverCompile(ast, keywords, out rejectedBy, out otherUses)) {
                JitRuntime.RecordScreenRejection(rejectedBy);
                return generic;
            }

            // over the typed body's parameters: the positionals, then a keyword method's keyword locals
            int typedCount = mandatoryCount + ((keywords != null) ? keywords.Count : ps.Optional.Length);
            bool[] storedOnly = null;
            for (int i = 0; i < typedCount; i++) {
                string name = (i < mandatoryCount) ? ((LocalVariable)ps.Mandatory[i]).Name
                    : (keywords != null) ? keywords.LocalNames[i - mandatoryCount]
                    : ((LocalVariable)ps.Optional[i - mandatoryCount].Left).Name;
                if (!otherUses.Contains(name)) {
                    if (storedOnly == null) { storedOnly = new bool[typedCount]; }
                    storedOnly[i] = true;
                }
            }

            JitStub stub;
            switch (arity) {
                case 0: stub = new JitStub0(ast, context, declaringModule, generic, keywords); break;
                case 1: stub = new JitStub1(ast, context, declaringModule, generic, keywords); break;
                case 2: stub = new JitStub2(ast, context, declaringModule, generic, keywords); break;
                case 3: stub = new JitStub3(ast, context, declaringModule, generic, keywords); break;
                case 4: stub = new JitStub4(ast, context, declaringModule, generic, keywords); break;
                case 5: stub = new JitStub5(ast, context, declaringModule, generic, keywords); break;
                default: stub = new JitStub6(ast, context, declaringModule, generic, keywords); break;
            }
            stub._hasDefault = hasDefault;
            stub._defaults = defaults;
            if (perCount) { stub._perCountMandatory = mandatoryCount; }
            stub._storedOnly = storedOnly;
            stub._declaringScope = declaringScope;
            var light = generic.Target as Microsoft.Scripting.Interpreter.LightLambda;
            if (light != null) {
                light.Compile += (sender, e) => stub._genericCompiled = true;
            } else {
                stub._genericCompiled = true;
            }
            return stub.Trampoline;
        }

        private static readonly object[] NoArgs = new object[0];
        protected static object[] Args0 { get { return NoArgs; } }
    }

    // ---- arity-specialized stubs -----------------------------------------------------------

    internal sealed class JitStub0 : JitStub {
        private volatile Func<object, Proc, object> _fast;
        private readonly Func<object, Proc, object>/*!*/ _gen;
        private readonly Func<object, Proc, object>/*!*/ _tramp;
        private Func<object, Proc, object> _cool;

        internal JitStub0(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 0, keywords) {
            _gen = (Func<object, Proc, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object>)entry; }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block); }
            long start = Stopwatch.GetTimestamp();
            object result = _gen(self, block);
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block) {
            var f = _fast;
            if (f != null) { return f(self, block); }
            Profile(self, Args0);
            f = _fast;
            return (f != null) ? f(self, block) : _gen(self, block);
        }
    }

    internal sealed class JitStub1 : JitStub {
        private volatile Func<object, Proc, object, object> _fast;
        private readonly Func<object, Proc, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object> _cool;

        internal JitStub1(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 1, keywords) {
            _gen = (Func<object, Proc, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object>)entry; }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0); }
            long start = Stopwatch.GetTimestamp();
            object result = _gen(self, block, a0);
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0) {
            var f = _fast;
            if (f != null) { return f(self, block, a0); }
            Profile(self, new[] { a0 });
            f = _fast;
            return (f != null) ? f(self, block, a0) : _gen(self, block, a0);
        }
    }

    internal sealed class JitStub2 : JitStub {
        private volatile Func<object, Proc, object, object, object> _fast;
        private readonly Func<object, Proc, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object> _cool;

        internal JitStub2(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 2, keywords) {
            _gen = (Func<object, Proc, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object>)entry; }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1); }
            long start = Stopwatch.GetTimestamp();
            object result = _gen(self, block, a0, a1);
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1); }
            Profile(self, new[] { a0, a1 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1) : _gen(self, block, a0, a1);
        }
    }

    internal sealed class JitStub3 : JitStub {
        private volatile Func<object, Proc, object, object, object, object> _fast;
        private readonly Func<object, Proc, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object, object> _cool;

        internal JitStub3(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 3, keywords) {
            _gen = (Func<object, Proc, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object>)entry; }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1, object a2) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1, a2); }
            long start = Stopwatch.GetTimestamp();
            object result = _gen(self, block, a0, a1, a2);
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1, object a2) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2); }
            Profile(self, new[] { a0, a1, a2 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2) : _gen(self, block, a0, a1, a2);
        }
    }

    internal sealed class JitStub4 : JitStub {
        private volatile Func<object, Proc, object, object, object, object, object> _fast;
        private readonly Func<object, Proc, object, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object, object, object> _cool;

        internal JitStub4(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 4, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object>)entry; }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1, object a2, object a3) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1, a2, a3); }
            long start = Stopwatch.GetTimestamp();
            object result = _gen(self, block, a0, a1, a2, a3);
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1, object a2, object a3) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2, a3); }
            Profile(self, new[] { a0, a1, a2, a3 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2, a3) : _gen(self, block, a0, a1, a2, a3);
        }
    }

    internal sealed class JitStub5 : JitStub {
        private volatile Func<object, Proc, object, object, object, object, object, object> _fast;
        private readonly Func<object, Proc, object, object, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object, object, object, object> _cool;

        internal JitStub5(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 5, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object, object>)entry; }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1, object a2, object a3, object a4) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1, a2, a3, a4); }
            long start = Stopwatch.GetTimestamp();
            object result = _gen(self, block, a0, a1, a2, a3, a4);
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1, object a2, object a3, object a4) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2, a3, a4); }
            Profile(self, new[] { a0, a1, a2, a3, a4 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2, a3, a4) : _gen(self, block, a0, a1, a2, a3, a4);
        }
    }

    internal sealed class JitStub6 : JitStub {
        private volatile Func<object, Proc, object, object, object, object, object, object, object> _fast;
        private readonly Func<object, Proc, object, object, object, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object, object, object, object, object> _cool;

        internal JitStub6(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 6, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object, object, object>)entry; }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1, object a2, object a3, object a4, object a5) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1, a2, a3, a4, a5); }
            long start = Stopwatch.GetTimestamp();
            object result = _gen(self, block, a0, a1, a2, a3, a4, a5);
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1, object a2, object a3, object a4, object a5) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2, a3, a4, a5); }
            Profile(self, new[] { a0, a1, a2, a3, a4, a5 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2, a3, a4, a5) : _gen(self, block, a0, a1, a2, a3, a4, a5);
        }
    }

}

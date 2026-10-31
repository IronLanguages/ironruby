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

        // profiling feedback
        private int _calls;
        private readonly int[]/*!*/ _argKind;       // 0 = unseen, 1 = int, 2 = double, 3 = other/mixed, 4 = long
        private RubyClass _receiverClass;
        private bool _polymorphicReceiver;
        private volatile bool _givenUp;

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
        }

        protected abstract void Install(Delegate/*!*/ entry);
        internal abstract Delegate/*!*/ Trampoline { get; }

        /// <summary>Called by the trampoline on every call until the method is specialized.</summary>
        protected void Profile(object self, object[] args) {
            if (_givenUp) { return; }

            if (_keywords != null) {
                // what the prologue binds the keywords to, if it binds them at all
                object kw = args[_positional];
                if (_keywords.Matches(kw)) {
                    for (int j = 0; j < _keywords.Count; j++) {
                        Observe(_positional + j, _keywords.Value(kw, j));
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

            if (++_calls < Threshold) { return; }
            _calls = 0;

            try {
                if (!TrySpecialize()) {
                    GiveUp();
                }
            } catch (Exception) {
                GiveUp();
            }
        }

        private void Observe(int i, object arg) {
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
            _givenUp = true;
            // Every method that gets here passed JitScreen, so each one is either a genuinely
            // type-dependent refusal or the screen and JitCompiler.Emit having drifted apart.
            // Name it, so a count that creeps up can be traced to the construct responsible.
            JitRuntime.Rejected++;
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit] declined {0}/{1} arity {2} (passed the pre-screen)",
                    _declaringModule.Name ?? "main", _ast.Name, _arity);
            }
            _direct = null;
            Install(_generic);
        }

        /// <summary>Deoptimize: drop the specialized entry and let profiling start over.</summary>
        internal void Invalidate() {
            JitRuntime.Invalidations++;
            _direct = null;
            // A method that was already declined must not fall back into profiling here.
            Install(_givenUp ? _generic : null);
            _calls = 0;
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
        internal void VersionChanged() {
            if (_givenUp) { return; }
            if (++_respecializations > MaxRespecializations) {
                _givenUp = true;
                _direct = null;
                Install(_generic);
                return;
            }
            Invalidate();
        }

        private bool TrySpecialize() {
            if (JitRuntime.Disabled || _context.HasRefinements) { return false; }
            if (_perCountMandatory >= 0) { return TrySpecializePerCount(); }

            var types = ProfiledTypes(_typedCount);

            long start = Stopwatch.GetTimestamp();
            var code = JitCompiler.TryCompile(_ast, _context, types, _keywords);
            if (code == null) { return false; }

            bool needsClassGuard = code.EmittedSelfCall;
            if (needsClassGuard) {
                if (_receiverClass == null || _polymorphicReceiver) { return false; }
                if (!SelfCallResolvesHere(_receiverClass)) { return false; }
            }

            int version = RubyModule.GlobalMethodVersion;

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

            var entry = BuildEntry(code, callee, version, needsClassGuard ? _receiverClass : null);
            Install(entry.Compile());
            if (_keywords != null && !needsClassGuard && _positional <= JitKeywordCall.MaxPositional &&
                _keywords.Count <= JitKeywordCall.MaxKeywords) {
                _direct = BuildDirectEntry(code, callee, version).Compile();
            }

            JitRuntime.CompileTicks += Stopwatch.GetTimestamp() - start;
            JitRuntime.Specialized++;
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit] specialized {0}/{1} arity {2} -> {3}{4}",
                    _declaringModule.Name ?? "main", _ast.Name, _arity, code.ReturnType, needsClassGuard ? " (self-call)" : "");
            }
            return true;
        }

        private JT[]/*!*/ ProfiledTypes(int count) {
            var types = new JT[count];
            for (int i = 0; i < count; i++) {
                if (_storedOnly != null && _storedOnly[i]) { types[i] = JT.Obj; continue; }
                switch (_argKind[i]) {
                    case 1: types[i] = JT.Int; break;
                    case 2: types[i] = JT.Dbl; break;
                    case 4: types[i] = JT.Lng; break;
                    default: types[i] = JT.Obj; break;
                }
            }
            return types;
        }

        /// <summary>
        /// One typed body for each number of optional arguments the calls so far have passed; a call
        /// passing a number no body was compiled for runs the generic body.
        /// </summary>
        private bool TrySpecializePerCount() {
            long start = Stopwatch.GetTimestamp();
            int optionalCount = _positional - _perCountMandatory;
            var codes = new JitCode[optionalCount + 1];
            var callees = new MSA.Expression[optionalCount + 1];
            bool any = false, pinned = false;
            for (int k = 0; k <= optionalCount; k++) {
                if ((_countsSeen & (1 << k)) == 0) { continue; }
                var code = JitCompiler.TryCompile(_ast, _context, ProfiledTypes(_perCountMandatory + k), null, k);
                if (code == null) { continue; }
                if (code.EmittedSelfCall) {
                    if (_receiverClass == null || _polymorphicReceiver || !SelfCallResolvesHere(_receiverClass)) { continue; }
                    pinned = true;
                }
                codes[k] = code;
                any = true;
            }
            if (!any) { return false; }

            int version = RubyModule.GlobalMethodVersion;
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

            Install(BuildPerCountEntry(codes, callees, version, pinned ? _receiverClass : null).Compile());

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

        private MSA.LambdaExpression/*!*/ BuildEntry(JitCode/*!*/ code, MSA.Expression/*!*/ callee, int version, RubyClass pinnedClass) {
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
                    case JT.Int: test = And(test, Ast.TypeIs(values[i], typeof(int))); break;
                    case JT.Dbl: test = And(test, Ast.TypeIs(values[i], typeof(double))); break;
                    // An Integer parameter carried as a long accepts either representation: a
                    // value in Int32 range arrives as an Int32 and only a larger one as an Int64.
                    case JT.Lng: test = And(test, Ast.Call(JitRuntime.M("IsIntegral"), values[i])); break;
                }
            }

            var typedArgs = new MSA.Expression[_typedCount + 1];
            typedArgs[0] = self;
            for (int i = 0; i < _typedCount; i++) {
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
                // backtrace.
                var result = Ast.Variable(typeof(object), "#result");
                var deopted = Ast.Constant(Deopted, typeof(object));
                fast = Ast.Block(typeof(object), new[] { result },
                    Ast.TryCatch(
                        Ast.Assign(result, fast),
                        Ast.Catch(typeof(JitDeoptException), Ast.Assign(result, deopted))
                    ),
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
                Ast.Call(JitRuntime.M("VersionOk"), Ast.Constant(version)),
                body,
                Ast.Block(
                    Ast.Call(JitRuntime.M("EntryStale"), Ast.Constant(this, typeof(object))),
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
            MSA.Expression/*!*/ callee, int version) {

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

            MSA.Expression test = Ast.Call(JitRuntime.M("VersionOk"), Ast.Constant(version));
            // the separation check on the last positional, as in BuildEntry
            if (_positional > 0 && code.ParameterTypes[_positional - 1] == JT.Obj) {
                test = And(test, Ast.Not(Ast.Call(typeof(RubyOps).GetMethod("IsKeywordArgumentsHash"), values[_positional - 1])));
            }
            var typedArgs = new MSA.Expression[_typedCount + 1];
            typedArgs[0] = self;
            for (int i = 0; i < _typedCount; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int:
                        test = And(test, Ast.TypeIs(values[i], typeof(int)));
                        typedArgs[i + 1] = Ast.Unbox(values[i], typeof(int));
                        break;
                    case JT.Dbl:
                        test = And(test, Ast.TypeIs(values[i], typeof(double)));
                        typedArgs[i + 1] = Ast.Unbox(values[i], typeof(double));
                        break;
                    case JT.Lng:
                        test = And(test, Ast.Call(JitRuntime.M("IsIntegral"), values[i]));
                        typedArgs[i + 1] = Ast.Call(JitRuntime.M("ToLong"), values[i]);
                        break;
                    default:
                        typedArgs[i + 1] = values[i];
                        break;
                }
            }

            MSA.Expression fast = Ast.Invoke(callee, typedArgs);
            if (code.ReturnType == JT.Lng) {
                fast = Ast.Call(JitRuntime.M("NarrowLong"), fast);
            } else if (fast.Type != typeof(object)) {
                fast = Ast.Convert(fast, typeof(object));
            }
            var noDirect = Ast.Field(null, typeof(JitKeywordCall).GetField("NoDirect"));
            if (code.CanDeopt) {
                fast = Ast.TryCatch(fast, Ast.Catch(typeof(JitDeoptException), noDirect));
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
        private MSA.LambdaExpression/*!*/ BuildPerCountEntry(JitCode[]/*!*/ codes, MSA.Expression[]/*!*/ callees, int version, RubyClass pinnedClass) {
            var self = Ast.Parameter(typeof(object), "self");
            var blk = Ast.Parameter(typeof(Proc), "block");
            var args = new MSA.ParameterExpression[_arity];
            for (int i = 0; i < _arity; i++) {
                args[i] = Ast.Parameter(typeof(object), "a" + i);
            }

            MSA.Expression body = null;
            for (int k = codes.Length - 1; k >= 0; k--) {
                MSA.Expression arm = (codes[k] != null)
                    ? BuildPerCountArm(codes[k], callees[k], self, blk, args, _perCountMandatory + k, pinnedClass)
                    : GenericCall(self, blk, args);
                body = (body == null) ? arm : Ast.Condition(
                    Ast.ReferenceEqual(args[_perCountMandatory + k], Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"))),
                    arm, body, typeof(object));
            }

            body = Ast.Condition(
                Ast.Call(JitRuntime.M("VersionOk"), Ast.Constant(version)),
                body,
                Ast.Block(
                    Ast.Call(JitRuntime.M("EntryStale"), Ast.Constant(this, typeof(object))),
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
            MSA.ParameterExpression/*!*/ blk, MSA.ParameterExpression[]/*!*/ args, int count, RubyClass pinnedClass) {

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
                // as in BuildEntry: the generic body runs after the handler, not inside it
                var result = Ast.Variable(typeof(object), "#result");
                var deopted = Ast.Constant(Deopted, typeof(object));
                fast = Ast.Block(typeof(object), new[] { result },
                    Ast.TryCatch(
                        Ast.Assign(result, fast),
                        Ast.Catch(typeof(JitDeoptException), Ast.Assign(result, deopted))
                    ),
                    Ast.Condition(Ast.ReferenceEqual(result, deopted), GenericCall(self, blk, args), result, typeof(object))
                );
            }
            return (test != null) ? Ast.Condition(test, fast, GenericCall(self, blk, args), typeof(object)) : fast;
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

        internal JitStub0(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 0, keywords) {
            _gen = (Func<object, Proc, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object>)entry; }

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

        internal JitStub1(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 1, keywords) {
            _gen = (Func<object, Proc, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object>)entry; }

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

        internal JitStub2(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 2, keywords) {
            _gen = (Func<object, Proc, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object>)entry; }

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

        internal JitStub3(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 3, keywords) {
            _gen = (Func<object, Proc, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object>)entry; }

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

        internal JitStub4(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 4, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object>)entry; }

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

        internal JitStub5(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 5, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object, object>)entry; }

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

        internal JitStub6(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 6, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object, object, object>)entry; }

        private object Call(object self, Proc block, object a0, object a1, object a2, object a3, object a4, object a5) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2, a3, a4, a5); }
            Profile(self, new[] { a0, a1, a2, a3, a4, a5 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2, a3, a4, a5) : _gen(self, block, a0, a1, a2, a3, a4, a5);
        }
    }

}

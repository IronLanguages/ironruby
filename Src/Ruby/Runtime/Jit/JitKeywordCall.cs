/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * Keyword calls straight into a specialized body.
 *
 * A keyword call builds a Hash for its keywords, and the entry of a specialized keyword
 * method (see JitKeywords) only reads the values back out of it. For the common shape -
 * a receiverless call, Symbol-literal keywords, a few positional arguments and nothing
 * else - the call is compiled with an inline cache in front of the dynamic call site:
 *
 *   r = JitKeywordCall.Invoke(cache, self, p0, p1, k0, k1, k2)
 *   r != JitKeywordCall.NoDirect ? r : <the call as it always was, keyword Hash and all>
 *
 * The cache remembers, per receiver class and global method version, which method the
 * name resolves to. When that is a method the JIT has specialized, and the keywords the
 * call passes are ones its prologue would bind without complaint, the values go straight
 * into the specialized body's "direct" entry (JitStub.Direct) in declaration order, with
 * the defaults of the keywords the call leaves out. Anything else - another kind of method,
 * a method not specialized (yet), a type guard or the method version failing, a deopt -
 * answers NoDirect and the call runs exactly as it did before, so the generic body raises
 * whatever ArgumentError there is to raise.
 *
 * A receiverless call may call a method of any visibility, so resolving the name on the
 * receiver's class with every visibility allowed is what the call site does too. A call with
 * an explicit receiver (other than `self') resolves the name with only public methods visible,
 * and so goes direct only to a method that is public where the lookup finds it: `private :m'
 * in a subclass, a singleton class or an including class leaves a forwarder there that hides
 * the inherited public m, as it does from the ordinary call. A private or protected method
 * makes the ordinary call, which raises or checks the caller as it always did. Calls
 * where refinements could be in play are never sent this way (JitRuntime.Disabled,
 * RubyContext.HasRefinements), and neither is anything once tracing is on.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Reflection;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;

    public sealed class JitKeywordCall {
        internal const int MaxPositional = 2;
        internal const int MaxKeywords = 3;

        // A site whose receiver keeps changing class (or that keeps meeting new method versions)
        // stops looking: resolving a name is far dearer than the hash it would save.
        private const int MaxMisses = 8;

        // A site with an explicit receiver whose callee has no direct entry for it to call - a
        // library method, a Ruby method the JIT declined, one whose signature the call does not
        // fit - goes quiet instead. Asking costs a call and the receiver's class - for an Integer,
        // Float or String a table lookup - on top of the ordinary call it makes anyway, which made
        // `2.5.round(half: :even)' 13-37% slower; so such a callee gets a few calls, and then the
        // site makes the ordinary call behind two field tests (Off, QuietAt) until the method
        // version changes. A redefinition can give the name a method the JIT takes, so the site
        // then resolves it again and gives it the same few calls; MaxMisses bounds how often that
        // can happen. A Ruby method the JIT is still profiling has no direct entry yet either, but
        // it gets one after JitStub.Threshold calls (counted over every site), so the site waits
        // longer for it.
        //
        // A receiverless site never goes quiet: its receiver is self, whose class is cheap to
        // find, and the same site meets another class whenever an inherited method runs on an
        // instance of another subclass - one whose method the JIT may well have taken. It keeps
        // asking, as it did before receiver calls were sent this way, and only MaxMisses stops it.
        private const int MaxDeadCalls = 4;
        private const int MaxUndecidedCalls = 4 * JitStub.Threshold;
        private const int NotQuiet = int.MinValue;

        /// <summary>"Not called here: make the call the ordinary way."</summary>
        public static readonly object NoDirect = new object();

        private readonly RubyContext/*!*/ _context;
        private readonly string/*!*/ _name;
        private readonly int _positional;
        private readonly RubySymbol/*!*/[]/*!*/ _keys;      // in the order the call passes them
        private readonly bool _publicOnly;                  // an explicit receiver: only a public method is called directly

        private sealed class State {
            internal RubyClass Class;
            internal int Version;
            internal JitStub Stub;          // null: the name does not resolve to a method this site can call directly
            internal int[] Slots;           // per declared keyword: the call's argument holding it, or -1 for its default
        }

        private volatile State _state;
        private int _misses;
        private int _deadCalls;
        private int _undecidedCalls;

        /// <summary>
        /// Set once the site has stopped looking for a direct entry for good (MaxMisses). The compiled
        /// call of a receiver site tests it, and <see cref="QuietAt"/>, before it calls <see cref="Invoke"/>,
        /// so from then on it costs those tests and nothing else; a receiverless one leaves the test to
        /// Invoke. (A stale read only means one more call through Invoke, which tests Off again and
        /// otherwise finds the same nothing to call.)
        /// </summary>
        public bool Off;

        /// <summary>
        /// The method version under which the site found nothing to call directly (MaxDeadCalls,
        /// MaxUndecidedCalls), or NotQuiet: it stays out of <see cref="Invoke"/> while
        /// RubyModule.GlobalMethodVersion is still that. Only a site with an explicit receiver
        /// sets it, and only its compiled call tests it.
        /// </summary>
        public int QuietAt = NotQuiet;

        private JitKeywordCall(RubyContext/*!*/ context, string/*!*/ name, int positional, RubySymbol/*!*/[]/*!*/ keys, bool publicOnly) {
            _context = context;
            _name = name;
            _positional = positional;
            _keys = keys;
            _publicOnly = publicOnly;
        }

        private static readonly VisibilityContext PublicOnly = new VisibilityContext(RubyMethodAttributes.Public);

        private static readonly MethodInfo/*!*/ InvokeMethod = typeof(JitKeywordCall).GetMethod("Invoke");
        private static readonly PropertyInfo/*!*/ GlobalMethodVersionProperty = typeof(RubyModule).GetProperty("GlobalMethodVersion");
        private static readonly FieldInfo/*!*/ NoDirectField = typeof(JitKeywordCall).GetField("NoDirect");
        private static readonly FieldInfo/*!*/ OffField = typeof(JitKeywordCall).GetField("Off");
        private static readonly FieldInfo/*!*/ QuietAtField = typeof(JitKeywordCall).GetField("QuietAt");

        public static object Invoke(object/*!*/ site, object self, object p0, object p1, object k0, object k1, object k2) {
            return ((JitKeywordCall)site).Call(self, p0, p1, k0, k1, k2);
        }

        private object Call(object self, object p0, object p1, object k0, object k1, object k2) {
            if (Off || JitRuntime.Disabled || _context.HasRefinements) { return NoDirect; }

            // (QuietAt is the compiled call's to test: it does not call this while it is current.)
            int version = RubyModule.GlobalMethodVersion;
            RubyClass cls = JitRuntime.ClassOf(_context, self);
            State state = _state;
            if (state == null || state.Class != cls || state.Version != version) {
                if (++_misses > MaxMisses) {
                    Off = true;
                    return NoDirect;
                }
                _state = state = Resolve(cls, version);
                // what the site is waiting for is this resolution's to give
                _deadCalls = 0;
                _undecidedCalls = 0;
            }

            JitStub stub = state.Stub;
            var direct = (stub != null) ? stub.Direct : null;
            if (direct == null) {
                // a stub the JIT gave up on never gets an entry; one it is still profiling may
                return (stub == null || stub.GivenUp)
                    ? Fruitless(ref _deadCalls, MaxDeadCalls, version)
                    : Fruitless(ref _undecidedCalls, MaxUndecidedCalls, version);
            }

            int[] slots = state.Slots;
            object d0 = null, d1 = null, d2 = null;
            switch (slots.Length) {
                case 3: d2 = Pick(stub, 2, slots[2], k0, k1, k2); goto case 2;
                case 2: d1 = Pick(stub, 1, slots[1], k0, k1, k2); goto case 1;
                case 1: d0 = Pick(stub, 0, slots[0], k0, k1, k2); break;
            }
            return direct(self, p0, p1, d0, d1, d2);
        }

        private object Fruitless(ref int count, int max, int version) {
            if (_publicOnly && ++count >= max) { QuietAt = version; }
            return NoDirect;
        }

        private static object Pick(JitStub/*!*/ stub, int declared, int slot, object k0, object k1, object k2) {
            switch (slot) {
                case 0: return k0;
                case 1: return k1;
                case 2: return k2;
                default: return stub.Keywords.Defaults[declared];
            }
        }

        /// <summary>
        /// What <see cref="_name"/> means on an instance of <paramref name="cls"/>, under method
        /// version <paramref name="version"/>, for this site.
        /// </summary>
        private State/*!*/ Resolve(RubyClass/*!*/ cls, int version) {
            var state = new State { Class = cls, Version = version };
            try {
                // A forwarder that `private :m' or `protected :m' left in a subclass resolves to the
                // inherited m, still public itself: the forwarder's own visibility is what hides it,
                // and only a lookup that sees nothing but public methods asks about it.
                var resolved = cls.ResolveMethod(_name, _publicOnly ? PublicOnly : VisibilityContext.AllVisible);
                var info = resolved.Found ? resolved.Info as RubyMethodInfo : null;
                if (info == null) { return state; }

                var stub = info.GetDelegate().Target as JitStub;
                if (stub == null || stub.Keywords == null || stub.Positional != _positional) { return state; }

                // The keywords this call passes must be ones the method's prologue binds without
                // raising: each a declared one, and every required one among them.
                var keywords = stub.Keywords;
                var slots = new int[keywords.Count];
                int matched = 0;
                for (int j = 0; j < slots.Length; j++) {
                    slots[j] = Array.IndexOf(_keys, keywords.Symbols[j]);
                    if (slots[j] >= 0) {
                        matched++;
                    } else if (keywords.Required[j]) {
                        return state;
                    }
                }
                if (matched != _keys.Length) { return state; }

                state.Stub = stub;
                state.Slots = slots;
            } catch (Exception) {
                state.Stub = null;
            }
            return state;
        }

        // ---- compilation of the call ---------------------------------------------------------

        /// <summary>
        /// The call `name(p0, p1, k: v, ...)' - or `receiver.name(...)', <paramref name="self"/> being the
        /// receiver then and <paramref name="implicitSelf"/> false - with the inline cache in front of it,
        /// or null when the call does not have that shape.
        /// </summary>
        internal static MSA.Expression TryTransform(AstGenerator/*!*/ gen, string/*!*/ methodName, MSA.Expression/*!*/ self,
            Arguments arguments, bool implicitSelf) {

            var options = gen.Context.RubyOptions;
            if (!options.Jit || options.EnableTracing || options.Profile || JitRuntime.Disabled) { return null; }
            if (arguments == null) { return null; }

            var exprs = arguments.Expressions;
            int count = exprs.Length;
            if (count == 0 || count - 1 > MaxPositional) { return null; }
            var hash = exprs[count - 1] as HashConstructor;
            if (hash == null || !hash.IsKeywordArguments) { return null; }
            var maplets = hash.Maplets;
            if (maplets.Length == 0 || maplets.Length > MaxKeywords) { return null; }

            var keys = new RubySymbol[maplets.Length];
            for (int i = 0; i < maplets.Length; i++) {
                var key = maplets[i].Key as SymbolLiteral;
                if (key == null || !(key.Value is string)) { return null; }
                keys[i] = gen.Context.CreateSymbol((string)key.Value, key.Encoding);
                // a repeated key is the hash's business
                if (Array.IndexOf(keys, keys[i], 0, i) >= 0) { return null; }
            }
            int positional = count - 1;
            for (int i = 0; i < positional; i++) {
                if (exprs[i] is SplattedArgument || exprs[i] is HashConstructor) { return null; }
            }

            var site = new JitKeywordCall(gen.Context, methodName, positional, keys, !implicitSelf);

            // The arguments are evaluated once, in source order, into temporaries both paths share.
            var temps = new List<MSA.ParameterExpression>();
            var body = new List<MSA.Expression>();
            var selfTemp = Ast.Variable(typeof(object), "#kwself");
            temps.Add(selfTemp);
            body.Add(Ast.Assign(selfTemp, AstUtils.Box(self)));

            var p = new MSA.Expression[MaxPositional];
            for (int i = 0; i < MaxPositional; i++) {
                if (i < positional) {
                    var t = Ast.Variable(typeof(object), "#kwp" + i);
                    temps.Add(t);
                    body.Add(Ast.Assign(t, AstUtils.Box(exprs[i].TransformRead(gen))));
                    p[i] = t;
                } else {
                    p[i] = Ast.Constant(null, typeof(object));
                }
            }
            var k = new MSA.Expression[MaxKeywords];
            for (int i = 0; i < MaxKeywords; i++) {
                if (i < maplets.Length) {
                    var t = Ast.Variable(typeof(object), "#kwv" + i);
                    temps.Add(t);
                    body.Add(Ast.Assign(t, AstUtils.Box(maplets[i].Value.TransformRead(gen))));
                    k[i] = t;
                } else {
                    k[i] = Ast.Constant(null, typeof(object));
                }
            }

            // the ordinary call: the keyword hash, then the dynamic site
            var hashArgs = new List<MSA.Expression>(1 + 2 * maplets.Length);
            hashArgs.Add(gen.CurrentScopeVariable);
            for (int i = 0; i < maplets.Length; i++) {
                hashArgs.Add(AstUtils.Box(Ast.Constant(keys[i])));
                hashArgs.Add(k[i]);
            }
            MethodInfo make;
            switch (maplets.Length) {
                case 1: make = Methods.MakeKeywordArgumentsHash1; break;
                case 2: make = Methods.MakeKeywordArgumentsHash2; break;
                default: make = Methods.MakeKeywordArgumentsHash3; break;
            }
            var makeHash = Ast.Call(make, hashArgs);
            var siteBuilder = new CallSiteBuilder(gen, selfTemp, null);
            for (int i = 0; i < positional; i++) {
                siteBuilder.Add(p[i]);
            }
            siteBuilder.Add(makeHash);
            var ordinary = siteBuilder.MakeCallAction(methodName, implicitSelf);

            var result = Ast.Variable(typeof(object), "#kwresult");
            temps.Add(result);
            MSA.Expression invoke = Ast.Call(InvokeMethod, Ast.Constant(site, typeof(object)), selfTemp, p[0], p[1], k[0], k[1], k[2]);
            if (!implicitSelf) {
                // A receiver site that went quiet is not to cost a call (MaxDeadCalls). A receiverless
                // one never goes quiet, and Invoke tests Off itself, so it pays nothing in front of it.
                var siteConstant = Ast.Constant(site);
                invoke = Ast.Condition(
                    Ast.OrElse(
                        Ast.Field(siteConstant, OffField),
                        Ast.Equal(Ast.Field(siteConstant, QuietAtField), Ast.Property(null, GlobalMethodVersionProperty))
                    ),
                    Ast.Field(null, NoDirectField),
                    invoke,
                    typeof(object)
                );
            }
            body.Add(Ast.Assign(result, invoke));
            body.Add(Ast.Condition(
                Ast.ReferenceNotEqual(result, Ast.Field(null, NoDirectField)),
                result,
                AstUtils.Convert(ordinary, typeof(object)),
                typeof(object)
            ));
            return Ast.Block(typeof(object), temps, body);
        }
    }
}

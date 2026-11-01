/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * Takes a Ruby method AST plus the types the profiling tier observed for its
 * parameters, and builds a *typed* LambdaExpression: Integer and Float values live in
 * CLR int/long/double locals rather than boxed objects, no RubyMethodScope or MutableTuple
 * is allocated, a call to the method itself becomes a direct call to the specialized
 * entry, and there are no dynamic call sites at all.
 *
 * Everything the compiler cannot prove makes it bail out (JitBailout), in which case
 * the method keeps running on the generic body.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    /// <summary>The type lattice. Deliberately tiny: this is a prototype.</summary>
    internal enum JT {
        None = 0,   // no value yet
        Never,      // bottom: the expression never produces a value (it jumped away). Unifies
                    // with anything, and is coerced by evaluating it and then falling into an
                    // unreachable default of whatever type the context wanted.
        Int,        // CLR int, an Integer that fits in an Int32
        Lng,        // CLR long, an Integer that fits in an Int64 but not an Int32 - and, inside
                    // the specialized region, any integer at all: integer arithmetic happens in
                    // long, and a result narrows back to Int only where it has to.
        Dbl,        // CLR double, a Float
        Bool,       // CLR bool, TrueClass/FalseClass
        Obj,        // boxed - anything, moved around but never operated on
    }

    internal sealed class JitBailout : Exception {
        internal static readonly JitBailout Instance = new JitBailout();
        private JitBailout() { }
    }

    /// <summary>The result of a successful specialization.</summary>
    internal sealed class JitCode {
        internal MSA.LambdaExpression/*!*/ Lambda;
        internal StrongBox<Delegate>/*!*/ SelfCell;
        internal JT ReturnType;
        internal JT[]/*!*/ ParameterTypes;
        internal bool CanDeopt;
        internal bool EmittedSelfCall;
    }

    internal sealed class JitCompiler {
        internal const int MaxNodes = 2000;

        private readonly MethodDeclaration _ast;
        private readonly RubyContext/*!*/ _context;
        private readonly int _scopeDepth;

        // Locals, in nested scopes. Ruby has no block scoping, but a local first assigned
        // inside a conditionally executed region is nil on the path that skipped it, which a
        // typed int/double slot cannot represent. Declaring such a local in the region's own
        // DLR block makes any read from outside fail the lookup and bail out, which is exactly
        // the condition under which the two scopings are indistinguishable.
        private readonly List<Dictionary<string, MSA.ParameterExpression>>/*!*/ _vars =
            new List<Dictionary<string, MSA.ParameterExpression>>();
        private readonly List<Dictionary<string, JT>>/*!*/ _types = new List<Dictionary<string, JT>>();
        private readonly List<List<MSA.ParameterExpression>>/*!*/ _declared = new List<List<MSA.ParameterExpression>>();

        private readonly MSA.ParameterExpression/*!*/ _self;
        private readonly JT[]/*!*/ _paramTypes;
        private readonly string/*!*/[]/*!*/ _paramNames;

        // Self-recursion goes through this cell, filled in once the lambda is compiled.
        private readonly StrongBox<Delegate>/*!*/ _selfCell = new StrongBox<Delegate>();
        private JT _selfReturn;
        private Type _selfDelegateType;

        // _pure: nothing in the body is observable part way through, so a deopt may restart it.
        // _canDeopt: the body contains an operation that can signal a deopt.
        private bool _pure = true;
        private bool _canDeopt;
        // The body writes an instance variable. A write to a frozen object deopts (JitRuntime.SetIVar)
        // so that the generic body raises the FrozenError from a real frame; it is always the first
        // write that does, before anything observable, so the restart is safe whatever _pure says.
        private bool _ivarWrite;
        private bool _selfCall;
        private int _nodes;

        // Explicit `return'. The label is created on the first one met, typed by the return type
        // the round is compiling under (_selfReturn); _returnUnion is what the values actually
        // seen so far unify to, which is how the round after this one is typed. _retype is set
        // when a return value cannot flow into the current guess at all, so the round is
        // abandoned and re-run against the wider type rather than rejected.
        private MSA.LabelTarget _returnLabel;
        private JT _returnUnion = JT.None;
        private JT _retype = JT.None;
        private bool _firstRound;

        /// <summary>The reason the last bailout gave, for IR_JIT_VERBOSE=1. Not on a fast path.</summary>
        private string _bailReason;

        private JitBailout/*!*/ Bail(string/*!*/ reason) {
            if (JitRuntime.Verbose && _bailReason == null) { _bailReason = reason; }
            return JitBailout.Instance;
        }

        // A method with keyword parameters: its keyword locals are typed parameters too, after the
        // positional ones, and the body starts after the prologue that binds them (JitKeywords).
        private readonly JitKeywords _keywords;

        // A body for calls that pass exactly this many of the optional parameters (JitStub's per-count
        // bodies): those are typed parameters, and the body starts by evaluating the defaults of the
        // rest, in order, as assignments to their locals. -1: every optional parameter is a typed
        // parameter, the entry having filled in the omitted ones' literal defaults.
        private readonly int _supplied = -1;

        private JitCompiler(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes, JitKeywords keywords, int supplied) {
            _ast = ast;
            _context = context;
            _paramTypes = paramTypes;
            _keywords = keywords;
            _supplied = (keywords == null) ? supplied : -1;
            _scopeDepth = ast.DefinedScope.Depth;
            _self = Ast.Parameter(typeof(object), "#self");
            PushScope();

            // The typed body's parameters are the generic delegate's: every mandatory one, then
            // the optional ones, whose defaults the entry has already filled in (JitStub).
            var mandatory = ast.Parameters.Mandatory;
            // The typed body's parameters are the generic delegate's positional ones - every mandatory
            // one, then the optional ones, whose defaults the entry has already filled in (JitStub) -
            // or, for a method with keywords, the mandatory ones and then its keyword locals.
            var optional = ast.Parameters.Optional;
            int expected = mandatory.Length + ((keywords != null) ? keywords.Count : (_supplied >= 0) ? _supplied : optional.Length);
            _paramNames = new string[paramTypes.Length];
            _paramExprs = new MSA.ParameterExpression[paramTypes.Length];
            _resolvedTypes = (JT[])paramTypes.Clone();
            if (paramTypes.Length != expected) { throw JitBailout.Instance; }
            for (int i = 0; i < paramTypes.Length; i++) {
                string name;
                if (i < mandatory.Length || keywords == null) {
                    var lv = (i < mandatory.Length) ? mandatory[i] as LocalVariable : optional[i - mandatory.Length].Left as LocalVariable;
                    if (lv == null) { throw JitBailout.Instance; }
                    name = lv.Name;
                } else {
                    name = keywords.LocalNames[i - mandatory.Length];
                }
                if (_vars[0].ContainsKey(name)) { throw JitBailout.Instance; }
                _paramNames[i] = name;
                if (keywords != null && i >= mandatory.Length && keywords.HasDefaultExpr(i - mandatory.Length)) {
                    // the boxed value or RubyOps.DefaultArgument; the local is bound by EmitKeywordDefaults
                    _paramExprs[i] = Ast.Parameter(typeof(object), "?" + name);
                    continue;
                }
                var p = Ast.Parameter(ClrType(paramTypes[i]), name);
                _paramExprs[i] = p;
                _vars[0].Add(name, p);
                _types[0].Add(name, paramTypes[i]);
            }
        }

        // The typed body's lambda parameters, and the types its entry unboxes and checks them to: a
        // keyword whose default the body evaluates is a boxed parameter, its type the local's (which
        // EmitKeywordDefaults decides when the profile has none).
        private readonly MSA.ParameterExpression[] _paramExprs;
        private JT[] _resolvedTypes;

        // ---- loop mode -------------------------------------------------------------------
        // -X:OSR points the same emitter at a `while' loop rather than a method body. The
        // difference is where the locals live: in a method they are parameters and CLR locals,
        // in a loop they are slots of the enclosing scope's MutableTuple, bound on demand as
        // the emitter meets them and loaded into typed CLR locals by the entry JitLoopCompiler
        // builds around this. Everything between those two edges is the same code.
        private readonly WhileLoopExpression _loop;
        private readonly Func<LocalVariable, JT, JitLoopSlot> _bindSlot;
        private readonly List<JitLoopSlot> _slots;
        private MSA.LabelTarget _breakLabel, _continueLabel;

        // Loop mode: what an instance variable of the loop's self holds right now, as a type
        // (see NumericIVar). Null when the loop was entered without its self, which is then
        // unbound and the loop may not touch self or an instance variable.
        private readonly Func<string, JT> _ivarProfile;

        // Loop mode: the instance variable a method name reads on the loop's self if that method
        // is an attr_reader there, else null (see EmitCall). Null when the loop has no self.
        private readonly Func<string, string> _attrProfile;

        // The attribute reads EmitCall turned into instance variable reads, and the variable each
        // reads, so that NumericIVar can type them the way it types the variable itself.
        private Dictionary<MethodCall, string> _attrReads;

        // Loop mode: a local's tuple slot - the expression that reads it, and what it holds right
        // now - without binding it to a typed slot (see EmitCall). The locals read this way, which
        // the loop then must not write: a write would go to a typed slot this read does not see.
        private readonly Func<LocalVariable, KeyValuePair<MSA.Expression, object>?> _peekLocal;
        private List<LocalVariable> _peeked;

        private JitCompiler(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context, int scopeDepth,
            Func<LocalVariable, JT, JitLoopSlot>/*!*/ bindSlot, MSA.ParameterExpression self, Func<string, JT> ivarProfile,
            Func<string, string> attrProfile, Func<LocalVariable, KeyValuePair<MSA.Expression, object>?> peekLocal) {
            _ast = null;
            _loop = loop;
            _context = context;
            _scopeDepth = scopeDepth;
            _bindSlot = bindSlot;
            _slots = new List<JitLoopSlot>();
            _paramTypes = new JT[0];
            _paramNames = new string[0];
            _self = self ?? Ast.Parameter(typeof(object), "#self");
            _ivarProfile = (self != null) ? ivarProfile : null;
            _attrProfile = (self != null) ? attrProfile : null;
            _peekLocal = peekLocal;
            PushScope();
        }

        /// <summary>
        /// Emits a typed body for one `while' loop. The slots it bound - the scope locals it
        /// touched, and the type each of them held when the loop turned hot - come back in
        /// <paramref name="slots"/>; the caller loads and stores them. <paramref name="self"/> is
        /// the variable the caller loads the loop's self into (null if it has none), and
        /// <paramref name="ivarProfile"/> what self's instance variables hold right now.
        /// </summary>
        internal static MSA.Expression TryCompileLoop(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context,
            int scopeDepth, Func<LocalVariable, JT, JitLoopSlot>/*!*/ bindSlot, out List<JitLoopSlot> slots, out bool canDeopt) {
            return TryCompileLoop(loop, context, scopeDepth, bindSlot, null, null, null, null, out slots, out canDeopt);
        }

        internal static MSA.Expression TryCompileLoop(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context,
            int scopeDepth, Func<LocalVariable, JT, JitLoopSlot>/*!*/ bindSlot, MSA.ParameterExpression self,
            Func<string, JT> ivarProfile, Func<string, string> attrProfile,
            Func<LocalVariable, KeyValuePair<MSA.Expression, object>?> peekLocal, out List<JitLoopSlot> slots, out bool canDeopt) {

            slots = null;
            canDeopt = false;
            var c = new JitCompiler(loop, context, scopeDepth, bindSlot, self, ivarProfile, attrProfile, peekLocal);
            MSA.Expression body;
            try {
                JT type;
                body = c.EmitWhile(loop, out type);
            } catch (JitBailout) {
                return null;
            }
            // A deopt restores the locals to what they were at the top of the iteration and
            // hands the loop back, so the iteration is re-run: it must not have been observable.
            if (c._canDeopt && !c._pure) { return null; }
            // An attribute receiver read straight from its tuple slot must not also be a typed
            // slot the loop writes (EmitCall).
            if (c._peeked != null) {
                foreach (var local in c._peeked) {
                    if (c._vars[0].ContainsKey(c.Key(local))) { return null; }
                }
            }
            slots = c._slots;
            // the back-edge safe point hands the loop back through the deopt
            // catch, so the catch is always needed - but it fires at the top of an iteration,
            // before anything observable, so it does not make an impure body unspecializable.
            canDeopt = true;
            return body;
        }

        /// <summary>The key a local is looked up under: names repeat across lexical depths.</summary>
        private string/*!*/ Key(LocalVariable/*!*/ local) {
            return (_loop == null) ? local.Name : local.Name + "@" + local.DefinitionLexicalDepth;
        }

        /// <summary>
        /// In loop mode a local the emitter has not met yet is bound to its tuple slot, typed
        /// by the value it holds right now - which is the only profile a loop that is already
        /// running needs.
        /// </summary>
        private bool BindLoopLocal(LocalVariable/*!*/ local, JT hint, out MSA.ParameterExpression p, out JT type) {
            var slot = _bindSlot(local, hint);
            if (slot == null) { p = null; type = JT.None; throw JitBailout.Instance; }
            _slots.Add(slot);
            _slotByVariable.Add(slot.Variable, slot);
            _vars[0].Add(Key(local), slot.Variable);
            _types[0].Add(Key(local), slot.Type);
            p = slot.Variable;
            type = slot.Type;
            return true;
        }

        private readonly Dictionary<MSA.ParameterExpression, JitLoopSlot> _slotByVariable =
            new Dictionary<MSA.ParameterExpression, JitLoopSlot>();

        /// <summary>
        /// The load of a slot the loop writes before it reads - a temporary of its own. What
        /// the slot held when the loop turned hot says nothing about what it holds on a later
        /// entry (the scope may be brand new, and the slot nil), so such a slot has no type
        /// guard; instead a flag says whether this run has written it. Reading it before that
        /// means the Ruby code is reading nil, which the typed body has no value for: deopt,
        /// and let the loop itself raise whatever it raises.
        /// </summary>
        private MSA.Expression/*!*/ LoadLoopSlot(MSA.ParameterExpression/*!*/ p) {
            JitLoopSlot slot;
            if (_loop != null && _slotByVariable.TryGetValue(p, out slot) && slot.Assigned != null) {
                _canDeopt = true;
                return Ast.Block(p.Type,
                    Ast.IfThen(Ast.Not(slot.Assigned), Ast.Throw(Ast.Call(JitRuntime.M("OsrUnsetSlot")))),
                    p
                );
            }
            return p;
        }

        /// <summary>The store into such a slot, which is what puts its flag up.</summary>
        private MSA.Expression/*!*/ StoreLoopSlot(MSA.ParameterExpression/*!*/ p, MSA.Expression/*!*/ rhs) {
            JitLoopSlot slot;
            if (_slotByVariable.TryGetValue(p, out slot) && slot.Assigned != null) {
                // The flag goes up after the right hand side has run, not before: `x += 1'
                // reads x, and that read has to still see the slot as unwritten.
                return Ast.Block(p.Type, Ast.Assign(p, rhs), Ast.Assign(slot.Assigned, Ast.Constant(true)), p);
            }
            return Ast.Assign(p, rhs);
        }

        private void PushScope() {
            _vars.Add(new Dictionary<string, MSA.ParameterExpression>());
            _types.Add(new Dictionary<string, JT>());
            _declared.Add(new List<MSA.ParameterExpression>());
        }

        private List<MSA.ParameterExpression>/*!*/ PopScope() {
            int last = _vars.Count - 1;
            var declared = _declared[last];
            _vars.RemoveAt(last);
            _types.RemoveAt(last);
            _declared.RemoveAt(last);
            return declared;
        }

        private bool TryLookup(string/*!*/ name, out MSA.ParameterExpression p, out JT type) {
            for (int i = _vars.Count - 1; i >= 0; i--) {
                if (_vars[i].TryGetValue(name, out p)) { type = _types[i][name]; return true; }
            }
            p = null;
            type = JT.None;
            return false;
        }

        private MSA.ParameterExpression/*!*/ Declare(string/*!*/ name, JT type) {
            var p = Ast.Parameter(ClrType(type), name);
            int last = _vars.Count - 1;
            _vars[last].Add(name, p);
            _types[last].Add(name, type);
            _declared[last].Add(p);
            return p;
        }

        /// <summary>Emits a conditionally executed statement list in its own local scope.</summary>
        private MSA.Expression/*!*/ EmitRegion(Statements statements, out JT type) {
            PushScope();
            var e = EmitStatements(statements, out type);
            var declared = PopScope();
            return declared.Count > 0 ? Ast.Block(ClrType(type), declared, e) : e;
        }

        private MSA.Expression/*!*/ EmitRegion(RExpr/*!*/ node, out JT type) {
            PushScope();
            var e = Emit(node, out type);
            var declared = PopScope();
            return declared.Count > 0 ? Ast.Block(ClrType(type), declared, e) : e;
        }

        private static Type/*!*/ ClrType(JT t) {
            switch (t) {
                case JT.Int: return typeof(int);
                case JT.Lng: return typeof(long);
                case JT.Dbl: return typeof(double);
                case JT.Bool: return typeof(bool);
                default: return typeof(object);
            }
        }

        /// <summary>
        /// Compiles a typed body for the given parameter types. Returns null when the method
        /// cannot be specialized.
        /// </summary>
        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes) {
            return TryCompile(ast, context, paramTypes, null);
        }

        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes, JitKeywords keywords) {
            return TryCompile(ast, context, paramTypes, keywords, -1);
        }

        /// <summary>
        /// <paramref name="supplied"/>: compile the body for calls that pass this many of the optional
        /// parameters, evaluating the other defaults itself; -1 for the body that takes them all.
        /// </summary>
        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes, JitKeywords keywords, int supplied) {
            // The self-recursive call needs the method's return type before the body has been
            // typed. Guess, compile, and re-run once if the guess was wrong.
            // Rounds: each retry widens the guess (Int -> Lng -> Dbl/Obj), so the lattice bounds
            // how many there can be; a guess that does not settle is rejected.
            JT guess = JT.None;
            for (int round = 0; round < 5; round++) {
                var c = new JitCompiler(ast, context, paramTypes, keywords, supplied);
                c._firstRound = (guess == JT.None);
                c._selfReturn = c._firstRound ? JT.Int : guess;
                c._selfDelegateType = DelegateType(LambdaParameterTypes(paramTypes, keywords, ast), c._selfReturn);
                if (c._selfDelegateType == null) { return null; }

                JT actual;
                MSA.Expression body;
                try {
                    body = c.EmitBody(out actual);
                } catch (JitBailout) {
                    // A `return' whose value does not fit the type this round assumed is not a
                    // rejection, it is a re-run against the type that does fit.
                    if (c._retype != JT.None && c._retype != c._selfReturn) { guess = c._retype; continue; }
                    if (JitRuntime.Verbose) {
                        Console.Error.WriteLine("[jit] rejected {0} arity {1}: {2}", ast.Name, paramTypes.Length,
                            c._bailReason ?? "unsupported construct");
                    }
                    return null;
                }

                // The method's value is the value it falls off with, unified with every value an
                // explicit `return' hands back.
                JT result = (c._returnUnion == JT.None) ? actual : Merge(actual, c._returnUnion);
                if (result != c._selfReturn) {
                    if (result == guess) { return null; }    // did not settle
                    guess = result;
                    continue;
                }

                // The return label carries every explicit `return'; the body falls into it.
                if (actual != result || c._returnLabel != null) {
                    body = c.Coerce(body, actual, result);
                }
                if (c._returnLabel != null) {
                    body = Ast.Label(c._returnLabel, body);
                }
                actual = result;

                // A body that can deopt mid-flight is only safe to restart if nothing it has
                // already done is observable.
                if (c._canDeopt && !c._pure) { return null; }

                var parameters = new List<MSA.ParameterExpression>(paramTypes.Length + 1);
                parameters.Add(c._self);
                var functionScope = new List<MSA.ParameterExpression>(c._declared[0]);
                for (int i = 0; i < c._paramNames.Length; i++) {
                    var p = c._paramExprs[i];
                    parameters.Add(p);
                    functionScope.Remove(p);
                }
                if (functionScope.Count > 0) {
                    body = Ast.Block(functionScope, body);
                }
                return new JitCode {
                    Lambda = Ast.Lambda(c._selfDelegateType, body, "jit$" + ast.Name, parameters),
                    SelfCell = c._selfCell,
                    ReturnType = actual,
                    ParameterTypes = c._resolvedTypes,
                    CanDeopt = c._canDeopt || c._ivarWrite,
                    EmittedSelfCall = c._selfCall,
                };
            }
            return null;
        }

        /// <summary>The CLR shape of the typed body's parameters: a keyword whose default it evaluates is boxed.</summary>
        private static JT[]/*!*/ LambdaParameterTypes(JT[]/*!*/ paramTypes, JitKeywords keywords, MethodDeclaration/*!*/ ast) {
            if (keywords == null || keywords.DefaultExprs == null) { return paramTypes; }
            var result = (JT[])paramTypes.Clone();
            int first = ast.Parameters.Mandatory.Length;
            for (int j = 0; j < keywords.Count && first + j < result.Length; j++) {
                if (keywords.HasDefaultExpr(j)) { result[first + j] = JT.Obj; }
            }
            return result;
        }

        /// <summary>Func&lt;object self, T1..Tn, TRet&gt;.</summary>
        private static Type DelegateType(JT[]/*!*/ ps, JT ret) {
            var types = new Type[ps.Length + 2];
            types[0] = typeof(object);
            for (int i = 0; i < ps.Length; i++) { types[i + 1] = ClrType(ps[i]); }
            types[ps.Length + 1] = ClrType(ret);
            switch (ps.Length) {
                case 0: return typeof(Func<,>).MakeGenericType(types);
                case 1: return typeof(Func<,,>).MakeGenericType(types);
                case 2: return typeof(Func<,,,>).MakeGenericType(types);
                case 3: return typeof(Func<,,,,>).MakeGenericType(types);
                case 4: return typeof(Func<,,,,,>).MakeGenericType(types);
                case 5: return typeof(Func<,,,,,,>).MakeGenericType(types);
                case 6: return typeof(Func<,,,,,,,>).MakeGenericType(types);
                // positionals plus keywords (JitStub.MaxArity - 1 + JitKeywords.MaxKeywords)
                default: return (ps.Length <= 14) ? Ast.GetFuncType(types) : null;
            }
        }

        // ---- emission ----------------------------------------------------------------------

        private MSA.Expression/*!*/ EmitBody(out JT type) {
            var body = _ast.Body;
            if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                throw JitBailout.Instance;
            }
            if (_keywords != null) {
                // the keyword prologue is what the entry did; the parameters hold its result
                var bind = EmitKeywordDefaults();
                var statements = EmitStatements(body.Statements, _keywords.PrologueLength, out type);
                if (bind != null) {
                    bind.Add(statements);
                    statements = Ast.Block(ClrType(type), bind);
                }
                return statements;
            }
            var defaults = EmitOmittedDefaults();
            var result = EmitInitialIVars(body.Statements, out type) ?? EmitStatements(body.Statements, out type);
            if (defaults != null) {
                defaults.Add(result);
                result = Ast.Block(ClrType(type), defaults);
            }
            return result;
        }

        /// <summary>
        /// A per-count body (_supplied): the defaults of the optional parameters the call left out,
        /// left to right, each an assignment to its local - which is what the generic prologue does,
        /// except that it first sets the local to nil, which only a default reading its own or a later
        /// parameter could see; such a read finds no local here and bails.
        /// </summary>
        private List<MSA.Expression> EmitOmittedDefaults() {
            if (_supplied < 0) { return null; }
            var optional = _ast.Parameters.Optional;
            if (_supplied >= optional.Length) { return null; }
            var parts = new List<MSA.Expression>(optional.Length - _supplied + 1);
            for (int i = _supplied; i < optional.Length; i++) {
                JT ignored;
                parts.Add(EmitAssignment(optional[i], out ignored));
            }
            return parts;
        }

        // Set while a keyword default is emitted: it may not assign a local (one bound after it, say).
        private bool _inKeywordDefault;

        /// <summary>
        /// The keywords whose default the body evaluates, in declaration order: each local is the value
        /// the call passed, or - RubyOps.DefaultArgument - its default. Its type is what the profile saw
        /// passed (which the entry checks), or the default's own type if nothing was ever passed.
        /// </summary>
        private List<MSA.Expression> EmitKeywordDefaults() {
            if (_keywords.DefaultExprs == null) { return null; }
            int first = _ast.Parameters.Mandatory.Length;
            var parts = new List<MSA.Expression>();
            var defaultArgument = Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"));
            for (int j = 0; j < _keywords.Count; j++) {
                if (!_keywords.HasDefaultExpr(j)) { continue; }
                int i = first + j;
                _inKeywordDefault = true;
                JT dt;
                var d = Emit(_keywords.DefaultExprs[j], out dt);
                _inKeywordDefault = false;
                if (dt == JT.None || dt == JT.Never) { throw Bail("keyword default without a value"); }

                JT pt = _paramTypes[i];
                JT lt = (pt == JT.None) ? dt : (pt == dt) ? pt : (IsInteger(pt) && IsInteger(dt)) ? JT.Lng : JT.Obj;
                if (lt == JT.Bool || lt == JT.None) { lt = JT.Obj; }
                _resolvedTypes[i] = lt;

                var p = _paramExprs[i];
                MSA.Expression passed;
                switch (lt) {
                    case JT.Int: passed = Ast.Unbox(p, typeof(int)); break;
                    case JT.Dbl: passed = Ast.Unbox(p, typeof(double)); break;
                    case JT.Lng: passed = Ast.Call(JitRuntime.M("ToLong"), p); break;
                    default: passed = p; break;
                }
                var local = Declare(_paramNames[i], lt);
                parts.Add(Ast.Assign(local, Ast.Condition(Ast.ReferenceEqual(p, defaultArgument), Coerce(d, dt, lt), passed, ClrType(lt))));
            }
            return parts;
        }

        /// <summary>An omitted optional parameter of a per-count body: a local its default was assigned to.</summary>
        private bool IsOmittedOptional(string/*!*/ name) {
            if (_supplied < 0) { return false; }
            var optional = _ast.Parameters.Optional;
            for (int i = _supplied; i < optional.Length; i++) {
                if (((LocalVariable)optional[i].Left).Name == name) { return true; }
            }
            return false;
        }

        // ---- an initializer's opening run of `@x = v' --------------------------------------
        // `def initialize(a, b) @a = a; @b = b; @n = 0 ...': a fresh object gets every one of these
        // variables at once - one slot array, already in its final shape, published with one CAS -
        // rather than one shape transition (and interlocked operation) per variable. Only for a run
        // of writes of distinct variables whose values have no effect and read no instance variable
        // (a parameter, a literal, self), so that doing all the stores together is the same as doing
        // them one by one. Any object that is not a fresh RubyObject takes the writes one by one.

        private const int MinInitialIVars = 2;

        private MSA.Expression EmitInitialIVars(Statements statements, out JT type) {
            type = JT.None;
            if (_loop != null || statements == null) { return null; }

            var run = new List<SimpleAssignmentExpression>();
            var names = new List<string>();
            foreach (var statement in statements) {
                var assign = statement as SimpleAssignmentExpression;
                var ivar = (assign != null && assign.Operation == null) ? assign.Left as InstanceVariable : null;
                if (ivar == null || names.Contains(ivar.Name) || !IsInitialIVarValue(assign.Right)) { break; }
                run.Add(assign);
                names.Add(ivar.Name);
            }
            if (run.Count < MinInitialIVars) { return null; }

            var site = JitRuntime.InitialIVarsSite.TryCreate(names.ToArray());
            if (site == null) { return null; }

            var temps = new MSA.ParameterExpression[run.Count];
            var parts = new List<MSA.Expression>();
            var stores = new List<MSA.Expression>();
            var oneByOne = new List<MSA.Expression>();
            var slots = Ast.Variable(typeof(object[]), "#slots");
            for (int i = 0; i < run.Count; i++) {
                ++_nodes;
                JT vt;
                var v = Emit(run[i].Right, out vt);
                temps[i] = Ast.Variable(typeof(object), "#iv" + i);
                parts.Add(Ast.Assign(temps[i], Coerce(v, vt, JT.Obj)));
                stores.Add(Ast.Assign(Ast.ArrayAccess(slots, Ast.Constant(i + 1)), temps[i]));
                oneByOne.Add(Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, temps[i], Ast.Constant(new InstanceVariableSite(names[i]))));
            }
            _pure = false;
            _ivarWrite = true;

            stores.Add(Ast.Call(JitRuntime.M("CommitInitialIVars"), _self, slots));
            parts.Add(Ast.Assign(slots, Ast.Call(JitRuntime.M("BeginInitialIVars"), _self, Ast.Constant(site))));
            parts.Add(Ast.IfThen(
                Ast.Not(Ast.AndAlso(Ast.NotEqual(slots, Ast.Constant(null, typeof(object[]))), Ast.Block(typeof(bool), stores))),
                Ast.Block(typeof(void), oneByOne)
            ));

            int rest = statements.Count - run.Count;
            if (rest == 0) {
                // the value of the last assignment is the method's
                type = JT.Obj;
                parts.Add(temps[run.Count - 1]);
            } else {
                int skip = run.Count;
                JT t = JT.Obj;
                int k = 0;
                foreach (var statement in statements) {
                    if (k++ < skip) { continue; }
                    parts.Add(Emit(statement, out t));
                }
                type = t;
            }

            var variables = new List<MSA.ParameterExpression>(temps);
            variables.Add(slots);
            return Ast.Block(ClrType(type), variables, parts);
        }

        /// <summary>A value an opening run of instance variable writes may have: see EmitInitialIVars.</summary>
        private bool IsInitialIVarValue(RExpr/*!*/ value) {
            if (value is SelfReference) { return true; }
            var literal = value as Literal;
            if (literal != null) {
                object v = literal.Value;
                return v == null || v is bool || v is int || v is long || v is double;
            }
            // Nothing before the run assigns a local but the omitted optionals' defaults, so a parameter
            // still holds its argument, and an omitted optional its default.
            var local = value as LocalVariable;
            return local != null && local.DefinitionLexicalDepth == _scopeDepth &&
                (Array.IndexOf(_paramNames, local.Name) >= 0 || IsOmittedOptional(local.Name));
        }

        private MSA.Expression/*!*/ EmitStatements(Statements/*!*/ statements, int start, out JT type) {
            int count = statements.Count - start;
            if (count <= 0) {
                type = JT.Obj;
                return Ast.Constant(null, typeof(object));
            }
            var parts = new List<MSA.Expression>(count);
            type = JT.None;
            for (int i = start; i < statements.Count; i++) {
                parts.Add(Emit(statements[i], out type));
            }
            return (parts.Count == 1) ? parts[0] : Ast.Block(ClrType(type), parts);
        }

        private MSA.Expression/*!*/ EmitStatements(Statements statements, out JT type) {
            if (statements == null || statements.Count == 0) {
                type = JT.Obj;
                return Ast.Constant(null, typeof(object));
            }
            if (statements.Count == 1) {
                return Emit(statements.First, out type);
            }
            var parts = new List<MSA.Expression>();
            foreach (var s in statements.AllButLast) {
                JT ignored;
                parts.Add(Emit(s, out ignored));
            }
            parts.Add(Emit(statements.Last, out type));
            return Ast.Block(ClrType(type), parts);
        }

        /// <summary>
        /// The node dispatch. ADDING A CASE HERE MEANS ADDING ONE TO <see cref="JitScreen"/> at
        /// the bottom of this file: the screen decides, before a method is ever wrapped, whether
        /// this method could accept its body, and a node kind it has not been told about is a
        /// method the JIT quietly stops speeding up. IR_JIT_VERBOSE=1 prints the histogram of
        /// node kinds the screen rejected, so the omission shows up there.
        /// </summary>
        private MSA.Expression/*!*/ Emit(RExpr/*!*/ node, out JT type) {
            if (++_nodes > MaxNodes) { throw Bail("too many nodes"); }

            var literal = node as Literal;
            if (literal != null) { return EmitLiteral(literal, out type); }

            // `:name': the Symbol the generic body would load, the same object on every evaluation
            var symbol = node as SymbolLiteral;
            if (symbol != null) {
                var name = symbol.Value as string;
                if (name == null) { throw Bail("symbol literal"); }
                type = JT.Obj;
                return Ast.Constant(_context.CreateSymbol(name, symbol.Encoding), typeof(object));
            }

            var local = node as LocalVariable;
            if (local != null) { return EmitLocalRead(local, out type); }

            var assign = node as SimpleAssignmentExpression;
            if (assign != null) { return EmitAssignment(assign, out type); }

            var call = node as MethodCall;
            if (call != null) { return EmitCall(call, out type); }

            var cond = node as ConditionalExpression;
            if (cond != null) { return EmitIfLike(cond.Condition, cond.TrueExpression, cond.FalseExpression, out type); }

            var ifExpr = node as IfExpression;
            if (ifExpr != null) { return EmitIf(ifExpr, out type); }

            var unless = node as UnlessExpression;
            if (unless != null) { return EmitUnless(unless, out type); }

            var not = node as NotExpression;
            if (not != null) {
                // `!x' is a call of x's #! - which the program may have redefined.
                JT operandType;
                var operand = EmitCondition(not.Expression, out operandType);
                RequireBuiltin(operandType, "!");
                type = JT.Bool;
                return Ast.Not(operand);
            }

            var and = node as AndExpression;
            if (and != null) { return EmitAndOr(and.Left, and.Right, true, out type); }

            var or = node as OrExpression;
            if (or != null) { return EmitAndOr(or.Left, or.Right, false, out type); }

            var ivar = node as InstanceVariable;
            if (ivar != null) {
                if (_loop != null && _ivarProfile == null) { throw Bail("instance variable in a loop entered without self"); }
                type = JT.Obj;
                return Ast.Call(JitRuntime.M("GetIVar"), Ast.Constant(_context), _self, Ast.Constant(new InstanceVariableSite(ivar.Name)));
            }

            var self = node as SelfReference;
            if (self != null) {
                if (_loop != null && _ivarProfile == null) { throw Bail("self in a loop entered without it"); }
                type = JT.Obj;
                return _self;
            }

            var loop = node as WhileLoopExpression;
            if (loop != null) { return EmitWhile(loop, out type); }

            if (_loop != null && _breakLabel != null) {
                var brk = node as BreakStatement;
                if (brk != null) {
                    if (brk.Arguments != null) { throw JitBailout.Instance; }
                    type = JT.Obj;
                    return Ast.Block(typeof(object), Ast.Break(_breakLabel), Ast.Constant(null, typeof(object)));
                }
                var nxt = node as NextStatement;
                if (nxt != null) {
                    if (nxt.Arguments != null) { throw JitBailout.Instance; }
                    type = JT.Obj;
                    return Ast.Block(typeof(object), Ast.Continue(_continueLabel), Ast.Constant(null, typeof(object)));
                }
            }

            var ret = node as ReturnStatement;
            if (ret != null) { return EmitReturn(ret, out type); }

            var arrayLiteral = node as ArrayConstructor;
            if (arrayLiteral != null) { return EmitArrayLiteral(arrayLiteral, out type); }

            var body = node as Body;
            if (body != null) {
                if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                    throw JitBailout.Instance;
                }
                return EmitStatements(body.Statements, out type);
            }

            throw Bail("node: " + node.GetType().Name);
        }

        /// <summary>
        /// `[a, b]': a new Array of boxed values, made by the helper the generic body calls. Building
        /// one has no effect anything could observe, so it is no obstacle to a deopt. A splat (or
        /// anything else Emit does not know) among the elements bails as usual.
        /// </summary>
        internal const int MaxArrayLiteral = 5;

        private MSA.Expression/*!*/ EmitArrayLiteral(ArrayConstructor/*!*/ node, out JT type) {
            var items = node.Arguments.Expressions;
            if (items.Length > MaxArrayLiteral) { throw Bail("array literal of " + items.Length); }
            var values = new List<MSA.Expression>(items.Length);
            foreach (var item in items) {
                JT t;
                var e = Emit(item, out t);
                values.Add(Coerce(e, t, JT.Obj));
            }
            type = JT.Obj;
            return IronRuby.Compiler.Methods.MakeArrayOpCall(values);
        }

        private MSA.Expression/*!*/ EmitLiteral(Literal/*!*/ literal, out JT type) {
            object v = literal.Value;
            if (v is int) { type = JT.Int; return Ast.Constant((int)v, typeof(int)); }
            if (v is long) { type = JT.Lng; return Ast.Constant((long)v, typeof(long)); }
            if (v is double) { type = JT.Dbl; return Ast.Constant((double)v, typeof(double)); }
            if (v is bool) { type = JT.Bool; return Ast.Constant((bool)v, typeof(bool)); }
            if (v == null) { type = JT.Obj; return Ast.Constant(null, typeof(object)); }
            throw Bail("literal: " + (v == null ? "nil" : v.GetType().Name));
        }

        private MSA.Expression/*!*/ EmitLocalRead(LocalVariable/*!*/ local, out JT type) {
            if (_loop == null && local.DefinitionLexicalDepth != _scopeDepth) { throw JitBailout.Instance; }
            MSA.ParameterExpression p;
            if (!TryLookup(Key(local), out p, out type)) {
                if (_loop != null) {
                    BindLoopLocal(local, JT.None, out p, out type);
                    return LoadLoopSlot(p);
                }
                // A local read before any assignment is nil in Ruby; not modelled.
                throw Bail("local read before assignment: " + local.Name);
            }
            return (_loop != null) ? LoadLoopSlot(p) : p;
        }

        private MSA.Expression/*!*/ EmitAssignment(SimpleAssignmentExpression/*!*/ node, out JT type) {
            string op = node.Operation;
            if (op == "&&" || op == "||") { throw JitBailout.Instance; }

            var ivarTarget = node.Left as InstanceVariable;
            if (ivarTarget != null) {
                if (op != null) { throw JitBailout.Instance; }
                // A write can raise (FrozenError), and an exception leaving an outlined loop
                // part way through would leave the scope's locals as they were on entry.
                if (_loop != null) { throw Bail("instance variable write in an outlined loop"); }
                JT vt;
                var v = Emit(node.Right, out vt);
                _pure = false;      // an ivar write is observable, so the body may not deopt
                _ivarWrite = true;
                type = JT.Obj;
                return Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, Coerce(v, vt, JT.Obj), Ast.Constant(new InstanceVariableSite(ivarTarget.Name)));
            }

            var target = node.Left as LocalVariable;
            if (target == null || (_loop == null && target.DefinitionLexicalDepth != _scopeDepth)) { throw JitBailout.Instance; }
            if (_inKeywordDefault) { throw Bail("keyword default assigns a local"); }

            JT rhsType;
            var rhs = Emit(node.Right, out rhsType);

            MSA.ParameterExpression p;
            JT lt;
            bool known = TryLookup(Key(target), out p, out lt);
            if (!known && _loop != null) {
                // The slot exists in the tuple whether or not the loop has read it yet, and it
                // is written back on the way out, so it has to be bound rather than declared.
                known = BindLoopLocal(target, rhsType, out p, out lt);
            }

            if (op != null) {
                // `x op= v'  ->  `x = x op v'
                if (!known) { throw JitBailout.Instance; }
                rhs = NumericIVar(node.Right, rhs, ref rhsType);
                if (!IsNumeric(lt) || !IsNumeric(rhsType)) { throw JitBailout.Instance; }
                rhs = EmitBinary(op, (_loop != null) ? LoadLoopSlot(p) : p, lt, rhs, rhsType, out rhsType);
            }

            if (known) {
                if (lt != rhsType) {
                    // Int and Lng are two representations of one Ruby class, so a store across
                    // them is a widening or a narrowing, not a type change. Anything else would
                    // change the value's class, which one typed slot cannot express.
                    if (!IsInteger(lt) || !IsInteger(rhsType)) { throw JitBailout.Instance; }
                    rhs = Coerce(rhs, rhsType, lt);
                    rhsType = lt;
                }
            } else {
                p = Declare(target.Name, rhsType);
            }
            type = rhsType;
            return (_loop != null) ? StoreLoopSlot(p, rhs) : (MSA.Expression)Ast.Assign(p, rhs);
        }

        private MSA.Expression/*!*/ EmitIf(IfExpression/*!*/ node, out JT type) {
            var clauses = node.ElseIfClauses;
            MSA.Expression elseExpr = Ast.Constant(null, typeof(object));
            JT elseType = JT.Obj;
            int i = (clauses != null) ? clauses.Count - 1 : -1;
            if (i >= 0 && clauses[i].Condition == null) {
                elseExpr = EmitRegion(clauses[i].Statements, out elseType);
                i--;
            }
            for (; i >= 0; i--) {
                if (clauses[i].Condition == null) { throw JitBailout.Instance; }
                JT thenType;
                var thenExpr = EmitRegion(clauses[i].Statements, out thenType);
                elseExpr = Join(EmitCondition(clauses[i].Condition), thenExpr, thenType, elseExpr, elseType, out elseType);
            }
            JT headType;
            var head = EmitRegion(node.Body, out headType);
            return Join(EmitCondition(node.Condition), head, headType, elseExpr, elseType, out type);
        }

        private MSA.Expression/*!*/ EmitUnless(UnlessExpression/*!*/ node, out JT type) {
            JT thenType;
            var thenExpr = EmitRegion(node.Statements, out thenType);
            MSA.Expression elseExpr = Ast.Constant(null, typeof(object));
            JT elseType = JT.Obj;
            if (node.ElseClause != null) {
                if (node.ElseClause.Condition != null) { throw JitBailout.Instance; }
                elseExpr = EmitRegion(node.ElseClause.Statements, out elseType);
            }
            return Join(Ast.Not(EmitCondition(node.Condition)), thenExpr, thenType, elseExpr, elseType, out type);
        }

        /// <summary>
        /// `while c; body; end' / `until c; body; end'. Any break/next/redo inside makes Emit
        /// bail, so the loop has exactly one exit and its value is always nil.
        /// </summary>
        private MSA.Expression/*!*/ EmitWhile(WhileLoopExpression/*!*/ node, out JT type) {
            if (node.IsPostTest) { throw JitBailout.Instance; }
            var exit = Ast.Label("#while-exit");
            var cont = Ast.Label("#while-continue");
            var savedBreak = _breakLabel;
            var savedContinue = _continueLabel;
            _breakLabel = exit;
            _continueLabel = cont;
            var test = EmitCondition(node.Condition);
            if (!node.IsWhileLoop) { test = Ast.Not(test); }
            JT ignored;
            var body = EmitRegion(node.Statements, out ignored);
            _breakLabel = savedBreak;
            _continueLabel = savedContinue;
            type = JT.Obj;

            // The outlined loop keeps a copy of its locals as they were at the top of the
            // iteration. A deopt part way through one restores them, so the loop is handed back
            // at an iteration boundary and the generic copy re-runs the iteration from there.
            MSA.Expression snapshot = (node == _loop) ? EmitSnapshot() : null;

            // The interrupt check (Thread#raise, Thread#kill, a trap handler), after the snapshot and the (pure) test, before the body.
            // An inner loop of an outlined one deopts to the *outer* iteration's top, which
            // re-runs part of that iteration: only allowed for a pure body, as for any deopt.
            if (_loop != null && node != _loop) { _canDeopt = true; }
            MSA.Expression safePoint = (_loop != null)
                ? Ast.Call(JitRuntime.M("OsrSafePoint"))
                : Ast.Call(RubyUtils.SafePointMethod);

            var iteration = (snapshot != null)
                ? Ast.Block(snapshot, Ast.IfThen(Ast.Not(test), Ast.Break(exit)), safePoint, body)
                : Ast.Block(Ast.IfThen(Ast.Not(test), Ast.Break(exit)), safePoint, body);

            return Ast.Block(typeof(object),
                Ast.Loop(iteration, exit, cont),
                Ast.Constant(null, typeof(object))
            );
        }

        /// <summary>
        /// `snap_x = x' for every slot bound so far. Emitted lazily, after the body, because
        /// the body is what binds them; the DLR block it produces runs before the test.
        /// </summary>
        private MSA.Expression/*!*/ EmitSnapshot() {
            if (_slots.Count == 0) { return AstUtils.Empty(); }
            var copies = new List<MSA.Expression>(_slots.Count);
            foreach (var slot in _slots) {
                copies.Add(Ast.Assign(slot.Snapshot, slot.Variable));
                if (slot.Assigned != null) {
                    copies.Add(Ast.Assign(slot.AssignedSnapshot, slot.Assigned));
                }
            }
            return Ast.Block(copies);
        }

        /// <summary>
        /// `return v' / `return'. The value leaves through a DLR return label typed by the
        /// return type this round is compiling under, which is the same type the value the body
        /// falls off with is given - so an early `return n' and a trailing `return f(n)' unify
        /// exactly the way the two branches of an `if' do. A round whose guess turns out to be
        /// too narrow for a value some `return' produces is abandoned and re-run wider.
        ///
        /// The value is JT.Never: control does not continue here, so whatever the context wanted
        /// this expression to be, it may be.
        /// </summary>
        private MSA.Expression/*!*/ EmitReturn(ReturnStatement/*!*/ node, out JT type) {
            // In loop mode the body being emitted is one loop outlined out of its method, entered
            // and left by the OSR site. A `return' there returns from the *method*, which the
            // outlined copy has no way to do.
            if (_loop != null) { throw Bail("return inside an outlined loop"); }

            var args = node.Arguments;
            JT vt;
            MSA.Expression value;
            if (args == null || args.Expressions.Length == 0) {
                vt = JT.Obj;
                value = Ast.Constant(null, typeof(object));
            } else if (args.Expressions.Length == 1 && !(args.Expressions[0] is SplattedArgument)) {
                value = Emit(args.Expressions[0], out vt);
            } else {
                throw Bail("return of several values");
            }

            if (vt == JT.Never) {
                // `return (return v)' - only the inner jump ever happens.
                type = JT.Never;
                return value;
            }

            _returnUnion = (_returnUnion == JT.None) ? vt : Merge(_returnUnion, vt);
            if (Merge(vt, _selfReturn) != _selfReturn) {
                // The first round's Int is an assumption, not an observation, so it does not
                // take part in the merge: a Float `return x' makes the next round Float, not Obj.
                _retype = _firstRound ? vt : Merge(_selfReturn, vt);
                throw Bail("return value wider than this round's return type");
            }

            if (_returnLabel == null) { _returnLabel = Ast.Label(ClrType(_selfReturn), "#return"); }
            type = JT.Never;
            return Ast.Return(_returnLabel, Coerce(value, vt, _selfReturn), typeof(object));
        }

        private MSA.Expression/*!*/ EmitIfLike(RExpr/*!*/ c, RExpr/*!*/ t, RExpr/*!*/ f, out JT type) {
            var test = EmitCondition(c);
            JT tt, ft;
            var te = EmitRegion(t, out tt);
            var fe = EmitRegion(f, out ft);
            return Join(test, te, tt, fe, ft, out type);
        }

        private MSA.Expression/*!*/ Join(MSA.Expression/*!*/ test, MSA.Expression/*!*/ a, JT at, MSA.Expression/*!*/ b, JT bt, out JT type) {
            type = Merge(at, bt);
            return Ast.Condition(test, Coerce(a, at, type), Coerce(b, bt, type), ClrType(type));
        }

        /// <summary>
        /// The type of a value that comes from one of two control-flow paths - the arms of an
        /// `if', the fall-off value and the explicit returns. Int and Lng are two
        /// representations of one Ruby class, so they merge to Lng. An Integer and a Float do
        /// NOT merge to Float: converting the Integer would change its class (`c ? 1.5 : 2' is
        /// 2, not 2.0), so a mixed pair is boxed.
        /// </summary>
        private static JT Merge(JT a, JT b) {
            if (a == b) { return a; }
            // A path that jumped away contributes no value, so the other one decides.
            if (a == JT.Never) { return b; }
            if (b == JT.Never) { return a; }
            if (IsInteger(a) && IsInteger(b)) { return JT.Lng; }
            return JT.Obj;
        }

        /// <summary>
        /// The operand type of a binary numeric operation: Ruby promotes an Integer operand of
        /// Float arithmetic to Float, so here - and only here - Int op Dbl is Dbl.
        /// </summary>
        private static JT Unify(JT a, JT b) {
            if (a == b) { return a; }
            if (IsNumeric(a) && IsNumeric(b)) {
                // int < long < double: the wider of the two wins.
                return (a == JT.Dbl || b == JT.Dbl) ? JT.Dbl : JT.Lng;
            }
            return JT.Obj;
        }

        private MSA.Expression/*!*/ Coerce(MSA.Expression/*!*/ e, JT from, JT to) {
            if (from == to) { return e; }
            if (from == JT.Never) {
                // e always jumps away, so what follows it is unreachable and only there to give
                // the expression the static type its context wants.
                return Ast.Block(ClrType(to), e, Ast.Default(ClrType(to)));
            }
            if (to == JT.Dbl && (from == JT.Int || from == JT.Lng)) { return Ast.Convert(e, typeof(double)); }
            if (to == JT.Lng && from == JT.Int) { return Ast.Convert(e, typeof(long)); }
            if (to == JT.Int && from == JT.Lng) {
                // Only where the value has to be an int again: an int local, an int parameter of
                // the direct self-call. Out of range means the Ruby value really did outgrow an
                // Int32, and the generic body takes it from there.
                _canDeopt = true;
                return FuseIntOp(e) ?? Ast.Call(JitRuntime.M("DemoteToInt"), e);
            }
            if (to == JT.Obj) {
                // A long leaves through the representation funnel: a value that fits in an Int32
                // IS an Int32, which is what #hash, #eql? and Hash keys are defined on.
                if (from == JT.Lng) { return Ast.Call(JitRuntime.M("NarrowLong"), e); }
                // Fixnum is a boxed int, Float a boxed double, true/false boxed bools. A literal
                // is boxed once, here, as the generic body does, rather than on every evaluation
                // (`@count = 0' in an initializer would otherwise allocate per object).
                var constant = e as MSA.ConstantExpression;
                if (constant != null) { return Ast.Constant(constant.Value, typeof(object)); }
                if (from == JT.Bool) { return Ast.Call(typeof(Microsoft.Scripting.Runtime.ScriptingRuntimeHelpers).GetMethod("BooleanToObject"), e); }
                return Ast.Convert(e, typeof(object));
            }
            throw JitBailout.Instance;
        }

        /// <summary>
        /// Ruby truthiness. A comparison already produces a CLR bool; an int/float value is
        /// always truthy.
        /// </summary>
        private MSA.Expression/*!*/ EmitCondition(RExpr/*!*/ node) {
            JT t;
            return EmitCondition(node, out t);
        }

        private MSA.Expression/*!*/ EmitCondition(RExpr/*!*/ node, out JT t) {
            // `a && b' / `a || b' tested for truth: only the truth of the operands matters
            var and = node as AndExpression;
            var or = node as OrExpression;
            if (and != null || or != null) {
                if (++_nodes > MaxNodes) { throw Bail("too many nodes"); }
                t = JT.Bool;
                var l = EmitCondition((and != null) ? and.Left : or.Left);
                PushScope();
                var r = EmitCondition((and != null) ? and.Right : or.Right);
                if (PopScope().Count > 0) { throw JitBailout.Instance; }
                return (and != null) ? Ast.AndAlso(l, r) : Ast.OrElse(l, r);
            }

            var e = Emit(node, out t);
            switch (t) {
                case JT.Bool: return e;
                case JT.Int:
                case JT.Lng:
                case JT.Dbl: return Ast.Block(e, Ast.Constant(true));
                default: throw Bail("condition of type " + t);
            }
        }

        /// <summary>
        /// `a && b' / `a || b' as a value: a when it decides the outcome - a falsy a for &&, a
        /// truthy one for || - and b otherwise. `n && n + 1' is n + 1, not true, and `n || 7' is n.
        /// The left operand has to be Bool or numeric, as a condition's has (a number is always
        /// truthy); the value's type is what the two outcomes merge to.
        /// </summary>
        private MSA.Expression/*!*/ EmitAndOr(RExpr/*!*/ left, RExpr/*!*/ right, bool isAnd, out JT type) {
            JT lt;
            var l = Emit(left, out lt);
            var value = Ast.Variable(ClrType(lt), "#andor");
            MSA.Expression truth;
            switch (lt) {
                case JT.Bool: truth = value; break;
                case JT.Int:
                case JT.Lng:
                case JT.Dbl: truth = Ast.Constant(true); break;
                default: throw Bail("&& / || operand of type " + lt);
            }

            PushScope();
            JT rt;
            var r = Emit(right, out rt);
            if (PopScope().Count > 0) { throw JitBailout.Instance; }

            var result = isAnd ? Join(truth, r, rt, value, lt, out type) : Join(truth, value, lt, r, rt, out type);
            return Ast.Block(ClrType(type), new[] { value }, Ast.Assign(value, l), result);
        }

        // ---- calls -------------------------------------------------------------------------

        private MSA.Expression/*!*/ EmitCall(MethodCall/*!*/ node, out JT type) {
            if (node.Block != null) { throw Bail("call with a block: " + node.MethodName); }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;

            // Self-recursion: `fib(n - 1)' inside `def fib'. Sound because the entry stub pins
            // the receiver class and the global method version, so no override can slip in.
            if (_ast != null && _keywords == null && node.Target == null && !node.IsVariableCall && node.MethodName == _ast.Name && argc == _paramNames.Length) {
                var callArgs = new MSA.Expression[argc + 1];
                callArgs[0] = _self;
                for (int i = 0; i < argc; i++) {
                    JT at;
                    var a = Emit(args.Expressions[i], out at);
                    callArgs[i + 1] = Coerce(a, at, _paramTypes[i]);
                }
                type = _selfReturn;
                _selfCall = true;
                return Ast.Invoke(
                    Ast.Convert(Ast.Field(Ast.Constant(_selfCell), "Value"), _selfDelegateType),
                    callArgs
                );
            }

            // `x' or `self.x' in an outlined loop, where x is an attr_reader on the loop's self: the
            // instance variable read the reader does, and no call. JitRuntime.ReadSelfAttr checks
            // self's class and the global method version on each read and deopts when either has
            // moved; a read is not observable, so the iteration stays restartable.
            if (argc == 0 && _attrProfile != null && (node.Target == null || node.Target is SelfReference)) {
                string variableName = _attrProfile(node.MethodName);
                if (variableName != null) {
                    _canDeopt = true;
                    if (_attrReads == null) { _attrReads = new Dictionary<MethodCall, string>(); }
                    _attrReads[node] = variableName;
                    type = JT.Obj;
                    return Ast.Call(JitRuntime.M("ReadSelfAttr"), Ast.Constant(_context), _self,
                        Ast.Constant(new JitRuntime.SelfAttrSite(node.MethodName, variableName, false)));
                }
            }

            // `o.x' in an outlined loop, o a local holding an object whose x is an attr_reader: the
            // same read, of o as it is in its tuple slot (the loop may not write o), typed by what
            // the variable holds in o right now - a guarded unboxing for an Integer or a Float.
            var receiverLocal = node.Target as LocalVariable;
            if (argc == 0 && receiverLocal != null && _peekLocal != null) {
                MSA.ParameterExpression bound;
                JT boundType;
                if (!TryLookup(Key(receiverLocal), out bound, out boundType)) {
                    var peek = _peekLocal(receiverLocal);
                    object live = peek.HasValue ? peek.Value.Value : null;
                    string variableName = (live != null)
                        ? JitRuntime.SelfAttrSite.ReaderVariable(JitRuntime.ClassOf(_context, live), node.MethodName, true) : null;
                    if (variableName != null) {
                        _canDeopt = true;
                        if (_peeked == null) { _peeked = new List<LocalVariable>(); }
                        _peeked.Add(receiverLocal);
                        MSA.Expression read = Ast.Call(JitRuntime.M("ReadSelfAttr"), Ast.Constant(_context), peek.Value.Key,
                            Ast.Constant(new JitRuntime.SelfAttrSite(node.MethodName, variableName, true)));
                        object value;
                        _context.TryGetInstanceVariable(live, variableName, out value);
                        string guard = (value is int) ? "GuardInt" : (value is long) ? "GuardLong" : (value is double) ? "GuardDouble" : null;
                        if (guard == null) { type = JT.Obj; return read; }
                        type = (value is int) ? JT.Int : (value is long) ? JT.Lng : JT.Dbl;
                        return Ast.Call(JitRuntime.M(guard), read);
                    }
                }
            }

            // Binary operators on Fixnum/Float.
            if (node.Target != null && argc == 1) {
                JT lt, rt;
                var l = Emit(node.Target, out lt);
                var r = Emit(args.Expressions[0], out rt);
                l = NumericIVar(node.Target, l, ref lt);
                r = NumericIVar(args.Expressions[0], r, ref rt);
                if (IsNumeric(lt) && IsNumeric(rt)) {
                    return EmitBinary(node.MethodName, l, lt, r, rt, out type);
                }
            }

            throw Bail("call: " + node.MethodName + "/" + argc);
        }

        private static bool IsNumeric(JT t) {
            return t == JT.Int || t == JT.Lng || t == JT.Dbl;
        }

        /// <summary>
        /// An instance variable read used as a numeric operand - `sum += @x', `@n * 2'. An
        /// outlined loop is compiled with its self at hand, so what the variable holds right now
        /// is a type profile: an Integer or a Float there is read through a guard that unboxes it
        /// while it still is one and deopts when it is not, and the generic loop then does
        /// whatever the operator does with whatever the variable holds. Reads are not
        /// observable, so this keeps a body restartable. Anything else stays an object.
        /// </summary>
        private MSA.Expression/*!*/ NumericIVar(RExpr/*!*/ node, MSA.Expression/*!*/ e, ref JT type) {
            var ivar = node as InstanceVariable;
            string name = (ivar != null) ? ivar.Name : null;
            var call = node as MethodCall;
            if (call != null && _attrReads != null) { _attrReads.TryGetValue(call, out name); }
            if (type != JT.Obj || name == null || _ivarProfile == null) { return e; }
            string guard;
            switch (_ivarProfile(name)) {
                case JT.Int: type = JT.Int; guard = "GuardInt"; break;
                case JT.Lng: type = JT.Lng; guard = "GuardLong"; break;
                case JT.Dbl: type = JT.Dbl; guard = "GuardDouble"; break;
                default: return e;
            }
            _canDeopt = true;
            return Ast.Call(JitRuntime.M(guard), e);
        }

        /// <summary>The two representations of a Ruby Integer the specialized code carries.</summary>
        private static bool IsInteger(JT t) {
            return t == JT.Int || t == JT.Lng;
        }

        /// <summary>
        /// `x = a op b' where a, b and x are all ints. Computing in long and narrowing back is
        /// correct but is a widening, an operation and a range check where one int operation with
        /// one check will do - and that shape is most of every int loop, so it is worth spotting:
        /// an int-typed operand pair under a narrowing back to int fuses into the int helper it
        /// came from. The two deopt on exactly the same values.
        /// </summary>
        private static MSA.Expression FuseIntOp(MSA.Expression/*!*/ e) {
            var bin = e as MSA.BinaryExpression;
            if (bin != null) {
                string name;
                switch (bin.NodeType) {
                    case MSA.ExpressionType.Add: name = "AddInt"; break;
                    case MSA.ExpressionType.Subtract: name = "SubInt"; break;
                    case MSA.ExpressionType.Multiply: name = "MulInt"; break;
                    default: return null;
                }
                var l = Narrowed(bin.Left);
                var r = Narrowed(bin.Right);
                return (l != null && r != null) ? Ast.Call(JitRuntime.M(name), l, r) : null;
            }
            var call = e as MSA.MethodCallExpression;
            if (call != null && call.Object == null && call.Arguments.Count == 2) {
                string name = (call.Method.Name == "DivLong") ? "DivInt"
                    : (call.Method.Name == "ModLong") ? "ModInt" : null;
                if (name == null) { return null; }
                var l = Narrowed(call.Arguments[0]);
                var r = Narrowed(call.Arguments[1]);
                return (l != null && r != null) ? Ast.Call(JitRuntime.M(name), l, r) : null;
            }
            return null;
        }

        /// <summary>The int this long operand was widened from, if it was one.</summary>
        private static MSA.Expression Narrowed(MSA.Expression/*!*/ e) {
            var unary = e as MSA.UnaryExpression;
            if (unary != null && unary.NodeType == MSA.ExpressionType.Convert
                && unary.Type == typeof(long) && unary.Operand.Type == typeof(int)) {
                return unary.Operand;
            }
            var constant = e as MSA.ConstantExpression;
            if (constant != null && constant.Type == typeof(long)) {
                long v = (long)constant.Value;
                if (v >= Int32.MinValue && v <= Int32.MaxValue) { return Ast.Constant((int)v, typeof(int)); }
            }
            return null;
        }

        // ---- the operators the body inlines must be the built-in ones ----------------------
        // Inlining `a + b' as a CLR add is only right while Integer#+ is the library method.
        // GlobalMethodVersion covers a redefinition AFTER specialization - the entry guard then
        // fails - but not one that happened before: a method that first turns hot after
        // `class Integer; def +(o) ... end; end' would otherwise inline the operator the program
        // replaced. So every operator is resolved, at compile time, on the class of its receiver,
        // and must be a library method registered under that very name (which also rejects
        // `alias_method :-, :+', a library method under someone else's name).

        private RubyClass/*!*/ ClassOfType(JT t, bool value) {
            object sample = (t == JT.Dbl) ? (object)0.0 : (t == JT.Bool) ? (object)value : (object)0;
            return JitRuntime.ClassOf(_context, sample);
        }

        private static bool IsBuiltinMethod(RubyClass/*!*/ cls, string/*!*/ name) {
            var resolved = cls.ResolveMethod(name, VisibilityContext.AllVisible);
            var info = resolved.Found ? resolved.Info as RubyLibraryMethodInfo : null;
            if (info == null) { return false; }
            foreach (var member in info.GetMembers()) {
                bool named = false;
                foreach (RubyMethodAttribute a in member.GetCustomAttributes(typeof(RubyMethodAttribute), false)) {
                    if (a.Name == name) { named = true; break; }
                }
                if (!named) { return false; }
            }
            return true;
        }

        // RequireBuiltin's answers so far in this compile. IsBuiltinMethod resolves the method and
        // reads custom attributes, and a long arithmetic body asks the same few questions hundreds
        // of times (ruby-bench's 30k_variables: most of the time it took to specialize). A body
        // asks about a handful of (type, operator) pairs, so a list searched from the front does;
        // a Dictionary keyed by a (JT, string) tuple cost the first compile 2-3 ms of instantiating
        // and JIT-compiling the generic code for that key.
        private sealed class BuiltinAnswer {
            internal readonly JT Type;
            internal readonly string/*!*/ Operator;
            internal readonly bool Ok;
            internal readonly BuiltinAnswer Next;
            internal BuiltinAnswer(JT type, string/*!*/ op, bool ok, BuiltinAnswer next) {
                Type = type; Operator = op; Ok = ok; Next = next;
            }
        }
        private BuiltinAnswer _builtinAnswers;

        /// <summary>`op' called on a receiver of type t is the built-in method; bail out if not.</summary>
        private void RequireBuiltin(JT t, string/*!*/ op) {
            BuiltinAnswer answer = _builtinAnswers;
            while (answer != null && (answer.Type != t || !String.Equals(answer.Operator, op))) {
                answer = answer.Next;
            }
            bool ok;
            if (answer != null) {
                ok = answer.Ok;
            } else {
                ok = (t == JT.Bool)
                    ? IsBuiltinMethod(ClassOfType(t, true), op) && IsBuiltinMethod(ClassOfType(t, false), op)
                    : IsBuiltinMethod(ClassOfType(t, false), op);
                // BasicObject#!= answers by calling ==.
                if (ok && op == "!=") { RequireBuiltin(t, "=="); }
                _builtinAnswers = new BuiltinAnswer(t, op, ok, _builtinAnswers);
            }
            if (!ok) { throw Bail("operator " + op + " on " + t + " is not the built-in one"); }
        }

        private MSA.Expression/*!*/ EmitBinary(string/*!*/ op, MSA.Expression/*!*/ l, JT lt, MSA.Expression/*!*/ r, JT rt, out JT type) {
            JT operand = Unify(lt, rt);
            if (operand == JT.Obj) { throw Bail("operator " + op + " on " + lt + ", " + rt); }
            // Ruby dispatches on the receiver: `1 + 2.0' is Integer#+.
            RequireBuiltin(lt, op);

            if (operand == JT.Dbl) {
                l = Coerce(l, lt, JT.Dbl);
                r = Coerce(r, rt, JT.Dbl);
                switch (op) {
                    case "+": type = JT.Dbl; return Ast.Add(l, r);
                    case "-": type = JT.Dbl; return Ast.Subtract(l, r);
                    case "*": type = JT.Dbl; return Ast.Multiply(l, r);
                    case "/": type = JT.Dbl; return Ast.Call(JitRuntime.M("DivDouble"), l, r);
                    case "%": type = JT.Dbl; return Ast.Call(JitRuntime.M("ModDouble"), l, r);
                    default: return EmitCompare(op, l, r, out type);
                }
            }

            // Integers. `operand' is Int exactly when both sides are, and that is the one case
            // in which the CLR operation cannot overflow a long and so needs no check at all:
            // two Int32s added, subtracted or multiplied in 64 bits always fit. So `int op int'
            // that overflows produces a long rather than deopting, which is what the runtime
            // does too - it is only the step out of Int64 that has nowhere typed left to go.
            bool bothInt = (operand == JT.Int);
            switch (op) {
                case "<": case ">": case "<=": case ">=": case "==": case "!=":
                    // A comparison never overflows, so two ints are compared as ints and a mixed
                    // pair widens to long. Both are exact.
                    return EmitCompare(op, Coerce(l, lt, operand), Coerce(r, rt, operand), out type);

                case "&": case "|": case "^": {
                    // Bitwise operations on integers stay within the range of their operands.
                    type = operand;
                    var bl = Coerce(l, lt, operand);
                    var br = Coerce(r, rt, operand);
                    if (op == "&") { return Ast.And(bl, br); }
                    if (op == "|") { return Ast.Or(bl, br); }
                    return Ast.ExclusiveOr(bl, br);
                }
            }

            l = Coerce(l, lt, JT.Lng);
            r = Coerce(r, rt, JT.Lng);
            type = JT.Lng;
            switch (op) {
                case "+": if (bothInt) { return Ast.Add(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("AddLong"), l, r);
                case "-": if (bothInt) { return Ast.Subtract(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("SubLong"), l, r);
                case "*": if (bothInt) { return Ast.Multiply(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("MulLong"), l, r);
                case "/": _canDeopt = true; return Ast.Call(JitRuntime.M("DivLong"), l, r);
                case "%": _canDeopt = true; return Ast.Call(JitRuntime.M("ModLong"), l, r);
                default: throw Bail("integer operator " + op);
            }
        }

        private MSA.Expression/*!*/ EmitCompare(string/*!*/ op, MSA.Expression/*!*/ l, MSA.Expression/*!*/ r, out JT type) {
            type = JT.Bool;
            switch (op) {
                case "<": return Ast.LessThan(l, r);
                case ">": return Ast.GreaterThan(l, r);
                case "<=": return Ast.LessThanOrEqual(l, r);
                case ">=": return Ast.GreaterThanOrEqual(l, r);
                case "==": return Ast.Equal(l, r);
                case "!=": return Ast.NotEqual(l, r);
                default: throw Bail("float operator " + op);
            }
        }
    }

    /// <summary>
    /// The static pre-screen, asked once per method before it is wrapped: *could* this body ever
    /// compile?
    ///
    /// Why it exists. Without it the only answer comes from TryCompile, which cannot run until
    /// the profiler has seen Threshold calls - and by then the trampoline is the delegate the
    /// runtime cached in RubyMethodBody._delegate and baked into call site rules, so even a
    /// perfect give-up leaves one permanent extra indirect call on a method the JIT never helps.
    /// Most rejections are not about types at all: `def fib(n) ... return n ... end' is declined
    /// only because Emit has no case for ReturnStatement, and no amount of profiling will change
    /// that. Those methods are better off never being wrapped.
    ///
    /// The contract. This walk rejects only what JitCompiler.Emit refuses for reasons that are
    /// visible in the syntax tree: a node kind it does not dispatch on, an operator EmitBinary
    /// does not know, a rescue/else/ensure, a `do' block, a post-test while, a local from an
    /// enclosing scope. Wherever Emit's answer depends on the *types* the profiler will observe -
    /// "is this operand numeric", "is this condition a bool", "did the self-call's return type
    /// settle" - the screen says yes. It is a pre-screen, not a decision: a false yes costs what
    /// the old code always cost, a false no costs a specialization.
    ///
    /// KEEPING IT HONEST. It is directly below Emit because the two have to move together, and
    /// each direction of drift is a statistic rather than silent slowness (IR_JIT_VERBOSE=1):
    ///   - too permissive (the screen passes a body Emit then refuses): the method is wrapped,
    ///     profiled and declined, which is JitRuntime.Rejected, and each one is named on stderr.
    ///     With the screen in place that count should be near zero.
    ///   - too strict (Emit grew a case the screen was not told about): the node kind appears in
    ///     the "screen rejected" histogram printed on the way out, so a newly supported node kind
    ///     sitting at the top of that histogram is the drift, visible without reading any code.
    /// </summary>
    internal sealed class JitScreen {
        private readonly int _scopeDepth;
        private readonly string/*!*/ _methodName;
        private readonly int _arity;
        private int _nodes;

        /// <summary>The node kind that said no, for the histogram.</summary>
        private string _rejectedBy;

        // Locals read anywhere other than straight into an instance variable (`@a = a'), or
        // assigned. A parameter not in here is only ever stored, so it is best carried boxed:
        // unboxing it on entry would only mean boxing it again for the store.
        private readonly HashSet<string>/*!*/ _otherUses = new HashSet<string>();

        private readonly JitKeywords _keywords;

        // walking a keyword default, which may not assign a local (JitCompiler.EmitKeywordDefaults)
        private bool _inKeywordDefault;

        private JitScreen(MethodDeclaration/*!*/ ast, JitKeywords keywords) {
            _scopeDepth = ast.DefinedScope.Depth;
            _methodName = ast.Name;
            // a method with keywords is never called directly from its typed body (EmitCall)
            _arity = (keywords != null) ? -1 : ast.Parameters.Mandatory.Length + ast.Parameters.Optional.Length;
            _keywords = keywords;
        }

        /// <summary>
        /// False when no assignment of parameter types could ever make TryCompile succeed for
        /// this body. <paramref name="rejectedBy"/> names the node kind that decided it.
        /// </summary>
        internal static bool CanEverCompile(MethodDeclaration/*!*/ ast, out string rejectedBy) {
            HashSet<string> otherUses;
            return CanEverCompile(ast, null, out rejectedBy, out otherUses);
        }

        /// <summary>
        /// <paramref name="otherUses"/>: the locals the body reads other than as the value of an
        /// instance variable assignment, or assigns.
        /// </summary>
        internal static bool CanEverCompile(MethodDeclaration/*!*/ ast, JitKeywords keywords, out string rejectedBy, out HashSet<string> otherUses) {
            var s = new JitScreen(ast, keywords);
            otherUses = s._otherUses;
            bool ok;
            try {
                ok = (keywords != null) ? s.WalkMethodBody(ast.Body, keywords.PrologueLength) : s.WalkBody(ast.Body);
                if (ok && keywords == null) {
                    // the defaults a per-count body evaluates (JitCompiler.EmitOmittedDefaults)
                    foreach (var optional in ast.Parameters.Optional) {
                        if (!s.Walk(optional.Right)) { ok = false; break; }
                    }
                } else if (ok && keywords.DefaultExprs != null) {
                    // the keyword defaults the typed body evaluates (JitCompiler.EmitKeywordDefaults)
                    s._inKeywordDefault = true;
                    foreach (var expr in keywords.DefaultExprs) {
                        if (expr != null && !s.Walk(expr)) { ok = false; break; }
                    }
                    s._inKeywordDefault = false;
                }
            } catch (Exception) {
                // A screen that throws must not take the process down with it; it just means the
                // method is not wrapped.
                ok = false;
                s._rejectedBy = "screen-error";
            }
            rejectedBy = s._rejectedBy;
            return ok;
        }

        private bool No(object/*!*/ node) {
            if (_rejectedBy == null) { _rejectedBy = node.GetType().Name; }
            return false;
        }

        /// <summary>Mirrors JitCompiler.EmitBody.</summary>
        private bool WalkBody(Body/*!*/ body) {
            if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                return No(body);
            }
            return WalkStatements(body.Statements);
        }

        /// <summary>Mirrors JitCompiler.EmitBody of a method with keywords: the prologue is not emitted.</summary>
        private bool WalkMethodBody(Body/*!*/ body, int prologueLength) {
            if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                return No(body);
            }
            for (int i = prologueLength; i < body.Statements.Count; i++) {
                if (!Walk(body.Statements[i])) { return false; }
            }
            return true;
        }

        private bool WalkStatements(Statements statements) {
            if (statements == null) { return true; }
            foreach (var s in statements) {
                if (!Walk(s)) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Mirrors JitCompiler.EmitCondition, whose operand has to end up Bool or numeric. Only
        /// the forms that are unconditionally JT.Obj can be ruled out here - `if @x', `while
        /// self', `x ? a : b' on nil - and those are common enough to be worth ruling out.
        /// </summary>
        private bool WalkCondition(RExpr/*!*/ node) {
            if (node is InstanceVariable || node is SelfReference || node is SymbolLiteral) { return No(node); }
            var literal = node as Literal;
            if (literal != null && literal.Value == null) { return No(node); }
            return Walk(node);
        }

        /// <summary>
        /// An operand of a binary operator, or the right side of `x op= v': EmitCall and
        /// EmitAssignment only go on when both sides are numeric, and an instance variable, self,
        /// or a nil/true/false literal never is - `@count + 1' is refused whatever gets profiled.
        /// </summary>
        private bool WalkOperand(RExpr/*!*/ node) {
            if (node is InstanceVariable || node is SelfReference || node is SymbolLiteral) { return No(node); }
            var literal = node as Literal;
            if (literal != null && !(literal.Value is int || literal.Value is long || literal.Value is double)) { return No(node); }
            return Walk(node);
        }

        /// <summary>
        /// Mirrors JitCompiler.Emit, case for case and in the same order. Read the two side by
        /// side; that is the point of them being in one file.
        /// </summary>
        private bool Walk(RExpr/*!*/ node) {
            // Emit gives up past MaxNodes. The screen's count is not exactly Emit's, so it only
            // rules out a body that is too big by any counting - the walk stays bounded either way.
            if (++_nodes > 2 * JitCompiler.MaxNodes) { return No(node); }

            var literal = node as Literal;
            if (literal != null) {
                object v = literal.Value;
                return (v is int || v is long || v is double || v is bool || v == null) || No(node);
            }
            if (node is SymbolLiteral) { return true; }

            var local = node as LocalVariable;
            if (local != null) {
                // A local from an enclosing scope is never read from a typed body. (Emit also
                // bails on a read before any assignment, which is a flow property: say yes.)
                _otherUses.Add(local.Name);
                return local.DefinitionLexicalDepth == _scopeDepth || No(node);
            }

            var assign = node as SimpleAssignmentExpression;
            if (assign != null) {
                string op = assign.Operation;
                if (op == "&&" || op == "||") { return No(node); }
                // `x op= v' becomes `x = x op v', so an operator EmitBinary does not know is out.
                if (op != null && !IsBinaryOperator(op)) { return No(node); }

                var ivarTarget = assign.Left as InstanceVariable;
                if (ivarTarget != null) {
                    if (op != null) { return No(node); }
                    var stored = assign.Right as LocalVariable;
                    if (stored != null && stored.DefinitionLexicalDepth == _scopeDepth) { ++_nodes; return true; }
                    return Walk(assign.Right);
                }
                var target = assign.Left as LocalVariable;
                if (target == null || target.DefinitionLexicalDepth != _scopeDepth || _inKeywordDefault) { return No(node); }
                _otherUses.Add(target.Name);
                return (op != null) ? WalkOperand(assign.Right) : Walk(assign.Right);
            }

            var call = node as MethodCall;
            if (call != null) { return WalkCall(call); }

            var cond = node as ConditionalExpression;
            if (cond != null) {
                return WalkCondition(cond.Condition) && Walk(cond.TrueExpression) && Walk(cond.FalseExpression);
            }

            var ifExpr = node as IfExpression;
            if (ifExpr != null) {
                var clauses = ifExpr.ElseIfClauses;
                if (clauses != null) {
                    for (int i = 0; i < clauses.Count; i++) {
                        var c = clauses[i];
                        if (c.Condition == null) {
                            // Only the last clause may be the `else'.
                            if (i != clauses.Count - 1) { return No(c); }
                        } else if (!WalkCondition(c.Condition)) {
                            return false;
                        }
                        if (!WalkStatements(c.Statements)) { return false; }
                    }
                }
                return WalkCondition(ifExpr.Condition) && WalkStatements(ifExpr.Body);
            }

            var unless = node as UnlessExpression;
            if (unless != null) {
                var els = unless.ElseClause;
                if (els != null) {
                    if (els.Condition != null) { return No(els); }
                    if (!WalkStatements(els.Statements)) { return false; }
                }
                return WalkCondition(unless.Condition) && WalkStatements(unless.Statements);
            }

            var not = node as NotExpression;
            if (not != null) { return WalkCondition(not.Expression); }

            var and = node as AndExpression;
            if (and != null) { return WalkCondition(and.Left) && WalkCondition(and.Right); }

            var or = node as OrExpression;
            if (or != null) { return WalkCondition(or.Left) && WalkCondition(or.Right); }

            if (node is InstanceVariable || node is SelfReference) { return true; }

            var loop = node as WhileLoopExpression;
            if (loop != null) {
                if (loop.IsPostTest) { return No(node); }
                return WalkCondition(loop.Condition) && WalkStatements(loop.Statements);
            }

            // `return' / `return v'. Emit's ReturnStatement case lands in a parallel change; the
            // screen already says yes so that the two merge into a working whole. Until then a
            // method with a `return' is wrapped and declined, which is what it always was. A
            // multi-value return builds an Array, which no typed body produces.
            var ret = node as ReturnStatement;
            if (ret != null) {
                var retArgs = ret.Arguments;
                if (retArgs == null || retArgs.Expressions.Length == 0) { return true; }
                if (retArgs.Expressions.Length != 1) { return No(node); }
                return Walk(retArgs.Expressions[0]);
            }

            var arrayLiteral = node as ArrayConstructor;
            if (arrayLiteral != null) {
                var items = arrayLiteral.Arguments.Expressions;
                if (items.Length > JitCompiler.MaxArrayLiteral) { return No(node); }
                foreach (var item in items) {
                    if (!Walk(item)) { return false; }
                }
                return true;
            }

            // break/next are only emitted in loop mode, which a method body never is, so they
            // fall through to the rejection below along with every other unknown node.

            var body = node as Body;
            if (body != null) { return WalkBody(body); }

            return No(node);
        }

        /// <summary>Mirrors JitCompiler.EmitCall: a self-recursive call, or a binary operator.</summary>
        private bool WalkCall(MethodCall/*!*/ node) {
            if (node.Block != null) { return No(node); }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;

            if (node.Target == null) {
                if (node.IsVariableCall || node.MethodName != _methodName || argc != _arity) { return No(node); }
            } else if (argc != 1 || !IsBinaryOperator(node.MethodName)) {
                // Emit only reaches EmitBinary for a one-argument call on a target, and only
                // EmitBinary's own operators survive there whatever the operand types are.
                return No(node);
            } else {
                return WalkOperand(node.Target) && WalkOperand(args.Expressions[0]);
            }

            // The self-call: each argument is coerced to its parameter's profiled type, which is
            // a question about types, so any argument Emit can emit at all will do.
            for (int i = 0; i < argc; i++) {
                if (!Walk(args.Expressions[i])) { return false; }
            }
            return true;
        }

        /// <summary>The operators JitCompiler.EmitBinary and EmitCompare between them handle.</summary>
        private static bool IsBinaryOperator(string/*!*/ op) {
            switch (op) {
                case "+": case "-": case "*": case "/": case "%":
                case "<": case ">": case "<=": case ">=": case "==": case "!=":
                case "&": case "|": case "^":
                    return true;
                default:
                    return false;
            }
        }
    }
}

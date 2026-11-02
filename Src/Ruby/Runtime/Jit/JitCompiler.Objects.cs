/* ****************************************************************************
 *
 * -X:JIT / -X:OSR: the emitter's object operations - constants, case/when, and the Array#[],
 * #fetch and #[]= and Hash#[], #[]=, #key? and #fetch intrinsics.
 *
 * Each turns what the generic code does with a dynamic call into a test or a call of a helper
 * (JitIntrinsics.cs) that answers exactly what the library method would, or deopts. The library
 * methods involved are checked here, when compiling, to be the library ones on every class whose
 * instances the helper answers for; the method version guard every specialization carries keeps
 * that true while it is installed. A constant is folded to the value it has when compiling, which
 * the constant version guard (JitCode.UsesConstants, JitLoopFacts.UsesConstants) keeps true.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;

    internal sealed partial class JitCompiler {
        private static readonly System.Reflection.MethodInfo/*!*/ IsTrueMethod =
            typeof(RubyOps).GetMethod("IsTrue", new[] { typeof(object) });

        // The scope the body's constants resolve in: a method's declaring scope, an outlined loop's
        // own. Null when there is none, and then no constant is folded.
        private readonly RubyScope _constantScope;
        private bool _usesConstants;

        // The body writes Hash or Array elements (EmitIndexWrite). In loop mode the iteration commits
        // the writes of the one before it at its top, through these two (see JitLoopFacts).
        private bool _hashWrite;
        private MSA.ParameterExpression _undoLog, _undoMark;

        // ---- constants ---------------------------------------------------------------------

        /// <summary>`C', `::C', `A::B': the value the constant has now, under the constant version guard.</summary>
        private MSA.Expression/*!*/ EmitConstant(ConstantVariable/*!*/ node, out JT type) {
            object value = ResolveConstant(node);
            if (value is int) { type = JT.Int; return Ast.Constant((int)value, typeof(int)); }
            if (value is long) { type = JT.Lng; return Ast.Constant((long)value, typeof(long)); }
            if (value is double) { type = JT.Dbl; return Ast.Constant((double)value, typeof(double)); }
            if (value is bool) { type = JT.Bool; return Ast.Constant((bool)value, typeof(bool)); }
            type = JT.Obj;
            return Ast.Constant(value, typeof(object));
        }

        /// <summary>
        /// What the constant resolves to right now, the way the generic code resolves it, or a bailout
        /// wherever that would not be a value the constant version alone keeps valid: no scope to
        /// resolve in, an autoload pending or running, a deprecated or private constant, a missing
        /// one, or a scope lexically inside an object's singleton class (whose generic sites do not
        /// cache either).
        /// </summary>
        private object ResolveConstant(ConstantVariable/*!*/ node) {
            var scope = _constantScope;
            if (scope == null) { throw Bail("constant without a scope: " + node.Name); }
            string name = node.Name;
            object value;
            if (node.Qualifier != null) {
                var qualifier = node.Qualifier as ConstantVariable;
                if (qualifier == null) { throw Bail("constant qualified by an expression: " + name); }
                var module = ResolveConstant(qualifier) as RubyModule;
                if (module == null) { throw Bail("constant qualified by a non-module: " + name); }
                using (_context.ClassHierarchyLocker()) {
                    RequireCacheableConstants(scope);
                    ConstantStorage storage;
                    // `A::B' finds B in A and A's ancestors; the generic code goes on to Object's for a
                    // module A, and raises for a private B: both are left to it
                    if (!module.TryResolveConstantNoLock(null, name, out storage) || module.IsPrivateConstantInAncestors(name)) {
                        throw Bail("qualified constant not found: " + name);
                    }
                    RequireNotDeprecated(module.GetConstantOwnerNoLock(name), name);
                    value = storage.Value;
                }
            } else {
                using (_context.ClassHierarchyLocker()) {
                    RequireCacheableConstants(scope);
                    ConstantStorage storage;
                    if (node.IsGlobal) {
                        var obj = _context.ObjectClass;
                        if (!obj.TryResolveConstantNoLock(null, name, out storage) || obj.IsPrivateConstant(name)) {
                            throw Bail("constant not found: ::" + name);
                        }
                        RequireNotDeprecated(obj.GetConstantOwnerNoLock(name), name);
                    } else {
                        RubyModule owner;
                        if (scope.TryResolveConstantNoLock(null, name, out storage, out owner) != null) {
                            throw Bail("constant not found: " + name);
                        }
                        // the owner is only reported once some constant has been deprecated
                        RequireNotDeprecated(owner, name);
                    }
                    value = storage.Value;
                }
            }
            // an autoload not yet run resolves to no value here; nil is not worth telling apart from it
            if (value == null || value is RubyModule.AutoloadedConstant) { throw Bail("constant without a value: " + name); }
            _usesConstants = true;
            return value;
        }

        /// <summary>A deprecated constant warns on every read, which the generic code does.</summary>
        private void RequireNotDeprecated(RubyModule owner, string/*!*/ name) {
            if (_context.HasDeprecatedConstants && (owner == null || owner.GetDeprecatedConstantOwnerNoLock(name) != null)) {
                throw Bail("deprecated constant " + name);
            }
        }

        private void RequireCacheableConstants(RubyScope/*!*/ scope) {
            if (_context.IsAutoloadInProgress) { throw Bail("autoload in progress"); }
            for (RubyScope s = scope; s != null; s = s.Parent) {
                var cls = s.Module as RubyClass;
                if (cls != null && cls.IsSingletonClass && !(cls.SingletonClassOf is RubyModule)) {
                    throw Bail("constant lexically in an object's singleton class");
                }
            }
        }

        // ---- case/when -----------------------------------------------------------------------

        /// <summary>The subject of a `case' being emitted, and what its when tests share.</summary>
        private sealed class CaseSubject {
            internal readonly MSA.ParameterExpression/*!*/ Value;
            internal readonly JT Type;
            // the subject's JitRuntime kind, computed once if a test asks for it
            internal MSA.ParameterExpression Kind;
            // a Symbol test asked for the subject not to be a ClrName (JitRuntime.CaseSymbol)
            internal bool SymbolTested;

            internal CaseSubject(MSA.ParameterExpression/*!*/ value, JT type) {
                Value = value;
                Type = type;
            }

            internal MSA.ParameterExpression/*!*/ KindVariable {
                get { return Kind ?? (Kind = Ast.Variable(typeof(int), "#case-kind")); }
            }
        }

        /// <summary>
        /// `case v when a, b then x when c then y else z end': `a === v || b === v ? x : c === v ? y : z'
        /// with v evaluated once and each `===' turned into a test (EmitWhenTest), and without a
        /// subject `a || b ? x : c ? y : z' on Ruby truthiness. A when the emitter cannot turn into a
        /// test - a splat, a regexp, anything but a constant or a literal - bails.
        /// </summary>
        private MSA.Expression/*!*/ EmitCase(CaseExpression/*!*/ node, out JT type) {
            var whens = node.WhenClauses;
            var variables = new List<MSA.ParameterExpression>();
            var init = new List<MSA.Expression>();
            CaseSubject subject = null;
            if (node.Value != null) {
                JT st;
                var v = Emit(node.Value, out st);
                if (st == JT.None || st == JT.Never) { throw Bail("case of a value that jumps away"); }
                var temp = Ast.Variable(ClrType(st), "#case");
                variables.Add(temp);
                init.Add(Ast.Assign(temp, v));
                subject = new CaseSubject(temp, st);
            }

            JT resultType;
            MSA.Expression result;
            if (node.ElseStatements != null) {
                result = EmitRegion(node.ElseStatements, out resultType);
            } else {
                result = Ast.Constant(null, typeof(object));
                resultType = JT.Obj;
            }

            for (int i = whens.Length - 1; i >= 0; i--) {
                var comparisons = whens[i].Comparisons;
                if (comparisons.Length == 0) { throw Bail("when without a comparison"); }
                MSA.Expression test = null;
                for (int j = comparisons.Length - 1; j >= 0; j--) {
                    if (++_nodes > MaxNodes) { throw Bail("too many nodes"); }
                    var t = (subject != null) ? EmitWhenTest(comparisons[j], subject) : EmitCondition(comparisons[j]);
                    test = (test == null) ? t : Ast.OrElse(t, test);
                }
                JT thenType;
                var then = EmitRegion(whens[i].Statements, out thenType);
                result = Join(test, then, thenType, result, resultType, out resultType);
            }
            type = resultType;

            if (subject != null) {
                if (subject.SymbolTested) {
                    init.Add(Ast.Call(JitRuntime.M("CaseSubjectNotClrName"), Coerce(subject.Value, subject.Type, JT.Obj)));
                }
                if (subject.Kind != null) {
                    variables.Add(subject.Kind);
                    init.Add(Ast.Assign(subject.Kind, Ast.Call(JitRuntime.M("CaseKind"), subject.Value)));
                }
            }
            if (init.Count == 0) { return result; }
            init.Add(result);
            return Ast.Block(ClrType(type), variables, init);
        }

        /// <summary>`expr === subject' as a CLR bool, for the forms of expr a case/when test can be.</summary>
        private MSA.Expression/*!*/ EmitWhenTest(RExpr/*!*/ expr, CaseSubject/*!*/ subject) {
            var constant = expr as ConstantVariable;
            if (constant != null) {
                object value = ResolveConstant(constant);
                var module = value as RubyModule;
                if (module != null) { return EmitModuleTest(module, subject); }
                if (value is int) { return EmitFixnumTest((int)value, subject); }
                throw Bail("when of a constant holding a " + value.GetType().Name);
            }

            var literal = expr as Literal;
            if (literal != null) {
                object v = literal.Value;
                if (v is int) { return EmitFixnumTest((int)v, subject); }
                if (v == null || v is bool) { return EmitImmediateTest(v, subject); }
                throw Bail("when of a " + v.GetType().Name + " literal");
            }

            var symbol = expr as SymbolLiteral;
            if (symbol != null) {
                var name = symbol.Value as string;
                if (name == null) { throw Bail("symbol literal"); }
                return EmitSymbolTest(_context.CreateSymbol(name, symbol.Encoding), subject);
            }

            var str = expr as StringLiteral;
            if (str != null) { return EmitStringTest(str, subject); }

            var range = expr as RangeExpression;
            if (range != null) { return EmitRangeTest(range, subject); }

            throw Bail("when of a " + expr.GetType().Name);
        }

        private RubyClass/*!*/ KindClass(int kind) {
            return JitRuntime.ClassOf(_context, JitRuntime.KindSample(_context, kind));
        }

        /// <summary>
        /// `M === v' for a module M whose singleton class answers === with Module#===, which is
        /// `v.kind_of?(M)'. A value whose JIT type is known is an instance of a known class, so the
        /// test folds to a constant; an object goes by its kind (see JitRuntime.CaseKind).
        /// </summary>
        private MSA.Expression/*!*/ EmitModuleTest(RubyModule/*!*/ module, CaseSubject/*!*/ subject) {
            if (!IsModuleCaseEquals(module)) { throw Bail("=== of " + module.Name + " is not Module#==="); }
            var cls = module as RubyClass;
            if (cls != null && cls.IsSingletonClass) { throw Bail("when of a singleton class"); }

            switch (subject.Type) {
                case JT.Int:
                case JT.Lng:
                    return Ast.Constant(KindClass(JitRuntime.KindFixnum).HasAncestor(module));
                case JT.Dbl:
                    return Ast.Constant(KindClass(JitRuntime.KindFloat).HasAncestor(module));
                case JT.Bool: {
                    bool t = KindClass(JitRuntime.KindTrue).HasAncestor(module);
                    bool f = KindClass(JitRuntime.KindFalse).HasAncestor(module);
                    if (t == f) { return Ast.Constant(t); }
                    return t ? (MSA.Expression)subject.Value : Ast.Not(subject.Value);
                }
            }

            int known = 0, truth = 0;
            for (int k = 1; k < JitRuntime.KindCount; k++) {
                // a singleton class can include a module, but its superclass chain is its class's
                if (k >= JitRuntime.FirstSingletonKind && !module.IsClass) { continue; }
                var kc = KindClass(k);
                if (kc.HasAdoptedInstances) { continue; }
                known |= 1 << k;
                if (kc.HasAncestor(module)) { truth |= 1 << k; }
            }
            var kind = subject.KindVariable;
            return Ast.Condition(
                Bit(known, kind),
                Bit(truth, kind),
                Ast.Call(JitRuntime.M("KindOf"), Ast.Constant(_context), Ast.Constant(new JitRuntime.KindOfSite(module)), subject.Value)
            );
        }

        /// <summary>Whether bit `kind' of the mask is set.</summary>
        private static MSA.Expression/*!*/ Bit(int mask, MSA.Expression/*!*/ kind) {
            return Ast.NotEqual(Ast.And(Ast.RightShift(Ast.Constant(mask), kind), Ast.Constant(1)), Ast.Constant(0));
        }

        /// <summary>`===' on the module's singleton class is ModuleOps.CaseEquals, Module#=== itself.</summary>
        private bool IsModuleCaseEquals(RubyModule/*!*/ module) {
            var meta = JitRuntime.ClassOf(_context, module);
            var resolved = meta.ResolveMethod("===", VisibilityContext.AllVisible);
            var info = resolved.Found ? resolved.Info as RubyLibraryMethodInfo : null;
            if (info == null) { return false; }
            foreach (var member in info.GetMembers()) {
                if (member.Name != "CaseEquals" || member.DeclaringType == null || member.DeclaringType.FullName != "IronRuby.Builtins.ModuleOps") {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// `L === v' for an Integer L: Integer#=== is Integer#==, which compares an Integer or a Float
        /// numerically and otherwise asks `v == L'.
        /// </summary>
        private MSA.Expression/*!*/ EmitFixnumTest(int literal, CaseSubject/*!*/ subject) {
            RequireBuiltin(JT.Int, "===");
            var v = subject.Value;
            switch (subject.Type) {
                case JT.Int: return Ast.Equal(v, Ast.Constant(literal));
                case JT.Lng: return Ast.Equal(v, Ast.Constant((long)literal));
                case JT.Dbl:
                    RequireBuiltin(JT.Dbl, "==");
                    return Ast.Equal(v, Ast.Constant((double)literal));
                case JT.Obj:
                    _canDeopt = true;
                    return Ast.Condition(
                        Ast.TypeIs(v, typeof(int)),
                        Ast.Equal(Ast.Unbox(v, typeof(int)), Ast.Constant(literal)),
                        Ast.Call(JitRuntime.M("CaseFixnum"), v, Ast.Constant(literal),
                            Ast.Constant(IsBuiltinMethod(KindClass(JitRuntime.KindFloat), "==")),
                            Ast.Constant(PlainEquals()),
                            // Integer#== asks the String, which may have a singleton == of its own
                            Ast.Constant(IsPlainLibraryMethod(KindClass(JitRuntime.KindString), "==") && NoToStr(KindClass(JitRuntime.KindFixnum))))
                    );
                default:
                    throw Bail("when of an Integer on a " + subject.Type);
            }
        }

        /// <summary>nil, true, false and a Symbol answer `==' by identity (theirs are the library methods).</summary>
        private bool PlainEquals() {
            return IsBuiltinMethod(KindClass(JitRuntime.KindNil), "==") && IsBuiltinMethod(KindClass(JitRuntime.KindTrue), "==")
                && IsBuiltinMethod(KindClass(JitRuntime.KindFalse), "==") && IsBuiltinMethod(KindClass(JitRuntime.KindSymbol), "==");
        }

        /// <summary>`nil === v', `true === v', `false === v': Kernel#=== is `self == v', which is identity for these.</summary>
        private MSA.Expression/*!*/ EmitImmediateTest(object literal, CaseSubject/*!*/ subject) {
            var cls = KindClass((literal == null) ? JitRuntime.KindNil : (bool)literal ? JitRuntime.KindTrue : JitRuntime.KindFalse);
            if (!IsBuiltinMethod(cls, "===") || !IsBuiltinMethod(cls, "==")) { throw Bail("=== of " + cls.Name + " is not the built-in one"); }
            var v = subject.Value;
            switch (subject.Type) {
                case JT.Int:
                case JT.Lng:
                case JT.Dbl:
                    return Ast.Constant(false);
                case JT.Bool:
                    if (literal == null) { return Ast.Constant(false); }
                    return (bool)literal ? (MSA.Expression)v : Ast.Not(v);
                case JT.Obj:
                    if (literal == null) { return Ast.Equal(v, Ast.Constant(null, typeof(object))); }
                    return Ast.AndAlso(Ast.TypeIs(v, typeof(bool)), Ast.Equal(Ast.Unbox(v, typeof(bool)), Ast.Constant((bool)literal)));
                default:
                    throw Bail("when of " + (literal ?? "nil") + " on a " + subject.Type);
            }
        }

        /// <summary>`:s === v': Symbol#=== is identity (a ClrName subject deopts, once per case).</summary>
        private MSA.Expression/*!*/ EmitSymbolTest(RubySymbol/*!*/ symbol, CaseSubject/*!*/ subject) {
            if (!IsBuiltinMethod(KindClass(JitRuntime.KindSymbol), "===")) { throw Bail("Symbol#=== is not the built-in one"); }
            if (subject.Type != JT.Obj) { return Ast.Constant(false); }
            subject.SymbolTested = true;
            _canDeopt = true;
            return Ast.ReferenceEqual(subject.Value, Ast.Constant(symbol, typeof(object)));
        }

        /// <summary>
        /// `"s" === v': String#=== compares a String's contents, and is false for anything that does
        /// not respond to to_str.
        /// </summary>
        private MSA.Expression/*!*/ EmitStringTest(StringLiteral/*!*/ literal, CaseSubject/*!*/ subject) {
            if (!IsBuiltinMethod(KindClass(JitRuntime.KindString), "===")) { throw Bail("String#=== is not the built-in one"); }
            switch (subject.Type) {
                case JT.Int:
                case JT.Lng:
                    if (!NoToStr(KindClass(JitRuntime.KindFixnum))) { throw Bail("Integer may respond to to_str"); }
                    return Ast.Constant(false);
                case JT.Dbl:
                    if (!NoToStr(KindClass(JitRuntime.KindFloat))) { throw Bail("Float may respond to to_str"); }
                    return Ast.Constant(false);
                case JT.Bool:
                    if (!NoToStr(KindClass(JitRuntime.KindTrue)) || !NoToStr(KindClass(JitRuntime.KindFalse))) { throw Bail("true/false may respond to to_str"); }
                    return Ast.Constant(false);
                case JT.Obj:
                    bool plainFalse = NoToStr(KindClass(JitRuntime.KindNil)) && NoToStr(KindClass(JitRuntime.KindTrue))
                        && NoToStr(KindClass(JitRuntime.KindFalse)) && NoToStr(KindClass(JitRuntime.KindFixnum))
                        && NoToStr(KindClass(JitRuntime.KindFloat)) && NoToStr(KindClass(JitRuntime.KindSymbol));
                    var str = literal.GetMutableString();
                    str.Freeze();
                    _canDeopt = true;
                    return Ast.Call(JitRuntime.M("CaseString"), subject.Value, Ast.Constant(str), Ast.Constant(plainFalse));
                default:
                    throw Bail("when of a String on a " + subject.Type);
            }
        }

        /// <summary>Instances of cls answer respond_to?(:to_str) false without calling Ruby code.</summary>
        private bool NoToStr(RubyClass/*!*/ cls) {
            return IsBuiltinMethod(cls, "respond_to?") && IsBuiltinMethod(cls, "respond_to_missing?")
                && !cls.ResolveMethod("to_str", VisibilityContext.AllVisible).Found;
        }

        /// <summary>
        /// `b..e === v' / `b...e === v' with Integer literal bounds: Range#=== is `b &lt;= v &amp;&amp;
        /// v &lt;= e' (or `&lt; e') through Integer#&lt;=&gt; and the subject's &lt;=&gt;. For a subject
        /// Integer#&lt;=&gt; cannot coerce, `b &lt;=&gt; v' is nil and the test false (see NoCoerce).
        /// </summary>
        private MSA.Expression/*!*/ EmitRangeTest(RangeExpression/*!*/ range, CaseSubject/*!*/ subject) {
            int b = RangeBound(range.Begin), e = RangeBound(range.End);
            bool exclusive = range.IsExclusive;
            if (!IsBuiltinMethod(_context.GetClass(typeof(IronRuby.Builtins.Range)), "===")) { throw Bail("Range#=== is not the built-in one"); }
            RequireBuiltin(JT.Int, "<=>");
            bool floats = IsBuiltinMethod(KindClass(JitRuntime.KindFloat), "<=>");

            var v = subject.Value;
            switch (subject.Type) {
                case JT.Int:
                case JT.Lng:
                    return InRange(v, Ast.Constant(b), Ast.Constant(e), exclusive);
                case JT.Dbl:
                    if (!floats) { throw Bail("Float#<=> is not the built-in one"); }
                    return InRange(v, Ast.Constant((double)b), Ast.Constant((double)e), exclusive);
                case JT.Obj:
                    _canDeopt = true;
                    return Ast.Condition(
                        Ast.TypeIs(v, typeof(int)),
                        InRange(Ast.Unbox(v, typeof(int)), Ast.Constant(b), Ast.Constant(e), exclusive),
                        Ast.Call(JitRuntime.M("CaseIntRange"), v, Ast.Constant(b), Ast.Constant(e), Ast.Constant(exclusive), Ast.Constant(floats),
                            Ast.Constant(NoCoerceKinds()))
                    );
                default:
                    throw Bail("when of a Range on a " + subject.Type);
            }
        }

        /// <summary>
        /// Integer#&lt;=&gt; answers nil for an instance of cls without calling Ruby code
        /// (Protocols.CoerceAndCompare): there is no #coerce to call, and nothing to ask first - respond_to?
        /// and method_missing are the library ones, on the class and on any singleton class of an instance.
        /// </summary>
        private bool NoCoerce(RubyClass/*!*/ cls) {
            return IsPlainLibraryMethod(cls, "respond_to?") && IsPlainLibraryMethod(cls, "method_missing")
                && HasPlainInstances(cls, "coerce") && !cls.ResolveMethod("coerce", VisibilityContext.AllVisible).Found;
        }

        /// <summary>The kinds (JitRuntime.CaseKind) of subject CaseIntRange answers false for, as a mask.</summary>
        private int NoCoerceKinds() {
            int mask = 0;
            foreach (int k in new[] { JitRuntime.KindNil, JitRuntime.KindTrue, JitRuntime.KindFalse, JitRuntime.KindSymbol,
                JitRuntime.KindString, JitRuntime.KindArray, JitRuntime.KindHash }) {
                if (NoCoerce(KindClass(k))) { mask |= 1 << k; }
            }
            return mask;
        }

        /// <summary>A bound of a when's range: an Integer literal, or a constant holding an Int32.</summary>
        private int RangeBound(RExpr/*!*/ bound) {
            var literal = bound as Literal;
            if (literal != null && literal.Value is int) { return (int)literal.Value; }
            var constant = bound as ConstantVariable;
            if (constant != null) {
                object value = ResolveConstant(constant);
                if (value is int) { return (int)value; }
            }
            throw Bail("when of a range whose bounds are not Integers");
        }

        private static MSA.Expression/*!*/ InRange(MSA.Expression/*!*/ v, MSA.Expression/*!*/ b, MSA.Expression/*!*/ e, bool exclusive) {
            if (v.Type != b.Type) {
                b = Ast.Convert(b, v.Type);
                e = Ast.Convert(e, v.Type);
            }
            return Ast.AndAlso(Ast.LessThanOrEqual(b, v), exclusive ? Ast.LessThan(v, e) : Ast.LessThanOrEqual(v, e));
        }

        // ---- numbers out of objects ---------------------------------------------------------

        /// <summary>
        /// `a[i] + 1', `s += h[k]', `x < v': one operand an object - an element, an object local - the
        /// other a number. The object is speculated to be an Integer, or next to a Float an Integer or
        /// a Float (both of which Float arithmetic treats alike), and the value that is not deopts. One
        /// the profile has only ever seen as something else (a String parameter, say) is not tried.
        /// </summary>
        private MSA.Expression/*!*/ EmitSpeculatedBinary(string/*!*/ op, MSA.Expression/*!*/ l, JT lt, RExpr lnode,
            MSA.Expression/*!*/ r, JT rt, RExpr rnode, out JT type) {
            if (!JitScreen.IsBinaryOperator(op)) { throw Bail("operator " + op + " on " + lt + ", " + rt); }
            bool leftObject = (lt == JT.Obj);
            JT other = leftObject ? rt : lt;
            RExpr objectNode = leftObject ? lnode : rnode;
            if (objectNode != null) {
                Type t = ObservedType(objectNode);
                if (t != null && t != MixedTypes && t != typeof(int) && t != typeof(long) && t != typeof(double)) {
                    throw Bail("operator " + op + " on a " + ((t == typeof(void)) ? "nil" : t.Name));
                }
            }
            MSA.Expression number;
            JT numberType;
            if (other == JT.Dbl) {
                // the receiver may be either: Integer#op(Float) and Float#op both have to be the library ones
                if (leftObject) { RequireBuiltin(JT.Int, op); }
                number = Ast.Call(JitRuntime.M("SpeculateDouble"), leftObject ? l : r);
                numberType = JT.Dbl;
            } else {
                number = Ast.Call(JitRuntime.M("SpeculateLong"), leftObject ? l : r);
                numberType = JT.Lng;
            }
            _canDeopt = true;
            return leftObject
                ? EmitBinary(op, number, numberType, r, rt, out type)
                : EmitBinary(op, l, lt, number, numberType, out type);
        }

        // ---- Array#[], Hash#[], #key?, #fetch, #[]= ------------------------------------------

        /// <summary>
        /// Instances of the CLR-backed class answer its own methods of this name: no singleton class
        /// of one of them (or a module it includes) has defined one, nor method_missing, and none has
        /// been given a Ruby subclass of the class as its class. The same test the binder makes before
        /// a rule may test only an object's CLR type (RubyCallAction.HasPlainClrTypeTest).
        /// </summary>
        private bool HasPlainInstances(RubyClass/*!*/ cls, string/*!*/ name) {
            using (_context.ClassHierarchyLocker()) {
                if (cls.HasAdoptedInstances) { return false; }
                var singletonMethods = cls.ClrSingletonMethods;
                return singletonMethods == null || !singletonMethods.ContainsKey(name) && !singletonMethods.ContainsKey("method_missing");
            }
        }

        private bool IsPlainLibraryMethod(RubyClass/*!*/ cls, string/*!*/ name) {
            return IsBuiltinMethod(cls, name) && HasPlainInstances(cls, name);
        }

        /// <summary>What the profile saw a method's arguments and self be (JitStub), or an outlined loop's self.</summary>
        internal sealed class ReceiverHints {
            internal Type[] Parameters;
            internal Type Self;
        }

        /// <summary>The CLR type a ReceiverHints entry has once two different ones were seen.</summary>
        internal static readonly Type/*!*/ MixedTypes = typeof(ReceiverHints);

        private ReceiverHints _hints;

        /// <summary>
        /// The CLR type the profile saw the receiver of an Array/Hash intrinsic be - a parameter of a
        /// method, self, a local of an outlined loop as it is now - typeof(void) for nil; null or
        /// MixedTypes if there is no single one. Only a hint: a receiver that turns out to be anything
        /// else is the helper's business.
        /// </summary>
        private Type ObservedType(RExpr/*!*/ receiver) {
            if (receiver is SelfReference) {
                return (_hints != null) ? _hints.Self : null;
            }
            var local = receiver as LocalVariable;
            if (local == null) { return null; }
            if (_loop != null) {
                if (_peekLocal == null) { return null; }
                var peek = _peekLocal(local);
                if (!peek.HasValue) { return null; }
                object value = peek.Value.Value;
                return (value == null) ? typeof(void) : value.GetType();
            }
            if (_hints == null || _hints.Parameters == null || local.DefinitionLexicalDepth != _scopeDepth) { return null; }
            int i = Array.IndexOf(_paramNames, local.Name);
            return (i >= 0 && i < _hints.Parameters.Length) ? _hints.Parameters[i] : null;
        }

        /// <summary>
        /// An intrinsic whose receiver has only ever been something it would deopt on - a String's
        /// size, a user object's [] - is not worth compiling: the generic code makes the call.
        /// </summary>
        private void RequireCollectionReceiver(RExpr/*!*/ receiver, bool arrays, bool hashes, string/*!*/ what) {
            Type t = ObservedType(receiver);
            if (t == null || t == MixedTypes || (arrays && t == typeof(RubyArray)) || (hashes && t == typeof(Hash))) { return; }
            throw Bail(what + " on a " + ((t == typeof(void)) ? "nil" : t.Name));
        }

        private RubyClass/*!*/ ArrayClass { get { return _context.GetClass(typeof(RubyArray)); } }
        private RubyClass/*!*/ HashClass { get { return _context.GetClass(typeof(Hash)); } }

        private bool _keyLookupChecked;

        /// <summary>
        /// A key the helpers find without calling Ruby is compared by Integer#eql?, Symbol#eql? or
        /// String#eql? where the comparer would dispatch: those must be the library methods. The
        /// comparer dispatches eql? on the key being looked up, and a String (unlike an Integer or
        /// a Symbol) can have a singleton eql? of its own, which the helpers do not look for: once
        /// any String has had one, none is looked up without dispatching.
        /// </summary>
        private bool KeyLookupIsPlain() {
            if (_keyLookupChecked) { return true; }
            if (!IsBuiltinMethod(KindClass(JitRuntime.KindFixnum), "eql?") || !IsBuiltinMethod(KindClass(JitRuntime.KindSymbol), "eql?")
                || !IsPlainLibraryMethod(KindClass(JitRuntime.KindString), "eql?")) {
                return false;
            }
            _keyLookupChecked = true;
            return true;
        }

        /// <summary>
        /// For which of Array and Hash the helper may stand in for `name' (and, for a Hash, also for
        /// the other methods listed): the flags the helpers take. Bails if for neither.
        /// </summary>
        private void RequireElementMethod(string/*!*/ name, string hashToo, out bool arrays, out bool hashes) {
            arrays = IsPlainLibraryMethod(ArrayClass, name);
            hashes = IsPlainLibraryMethod(HashClass, name) && (hashToo == null || IsPlainLibraryMethod(HashClass, hashToo)) && KeyLookupIsPlain();
            if (!arrays && !hashes) { throw Bail(name + " is not the built-in one"); }
        }

        /// <summary>`recv[index]': Array#[] or Hash#[] (and Hash#default, which a miss answers), decided by the helper at run time.</summary>
        private MSA.Expression/*!*/ EmitIndexRead(RExpr/*!*/ receiver, MSA.Expression/*!*/ recv, JT recvType, MSA.Expression/*!*/ index, JT indexType,
            out JT type) {
            if (recvType != JT.Obj) { throw Bail("[] on a " + recvType); }
            string helper;
            switch (indexType) {
                case JT.Int: helper = "IndexInt"; break;
                case JT.Lng: helper = "IndexLong"; break;
                case JT.Obj: helper = "IndexObj"; break;
                default: throw Bail("[] with a " + indexType + " index");
            }
            bool arrays, hashes;
            RequireElementMethod("[]", "default", out arrays, out hashes);
            RequireCollectionReceiver(receiver, arrays, hashes, "[]");
            _canDeopt = true;
            type = JT.Obj;
            return Ast.Call(JitRuntime.M(helper), recv, index, Ast.Constant(arrays), Ast.Constant(hashes));
        }

        internal static bool IsHashQuery(string/*!*/ name, int argc) {
            switch (name) {
                case "key?":
                case "has_key?":
                    return argc == 1;
                case "fetch":
                    return argc == 1 || argc == 2;
                case "size":
                case "length":
                case "empty?":
                    return argc == 0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Hash#fetch is the prelude's (it raises KeyError, which the library method does not): for
        /// fetch(k) `key?(k) ? self[k] : raise', for fetch(k, d) the library method under the name
        /// fetch_raising_index_error, which answers d for a key that is not there.
        /// </summary>
        private bool IsPreludeHashFetch() {
            var hash = HashClass;
            var resolved = hash.ResolveMethod("fetch", VisibilityContext.AllVisible);
            var info = resolved.Found ? resolved.Info as RubyMethodInfo : null;
            if (info == null || info.DeclaringModule != hash || info.Document == null || !RubyStackTraceBuilder.IsInternalFile(info.Document.FileName)) {
                return false;
            }
            var original = hash.ResolveMethod("fetch_raising_index_error", VisibilityContext.AllVisible);
            var library = original.Found ? original.Info as RubyLibraryMethodInfo : null;
            if (library == null) { return false; }
            foreach (var member in library.GetMembers()) {
                if (member.Name != "Fetch" || member.DeclaringType == null || member.DeclaringType.FullName != "IronRuby.Builtins.IDictionaryOps") {
                    return false;
                }
            }
            return HasPlainInstances(hash, "fetch") && HasPlainInstances(hash, "fetch_raising_index_error")
                && IsPlainLibraryMethod(hash, "key?") && IsPlainLibraryMethod(hash, "[]");
        }

        /// <summary>
        /// `h.key?(k)', `h.has_key?(k)' on an exact Hash, and `x.fetch(k)', `x.fetch(k, default)',
        /// `x.size', `x.length', `x.empty?' on an exact Array or Hash.
        /// </summary>
        private MSA.Expression/*!*/ EmitHashQuery(MethodCall/*!*/ node, out JT type) {
            var args = (node.Arguments != null) ? node.Arguments.Expressions : new RExpr[0];
            foreach (var arg in args) {
                if (arg is SplattedArgument) { throw Bail(node.MethodName + " with a splat"); }
            }
            JT recvType;
            var recv = Emit(node.Target, out recvType);
            if (recvType != JT.Obj) { throw Bail(node.MethodName + " on a " + recvType); }
            _canDeopt = true;

            string name = node.MethodName;
            if (args.Length == 0) {
                bool arrays, hashes;
                RequireElementMethod(name, null, out arrays, out hashes);
                RequireCollectionReceiver(node.Target, arrays, hashes, name);
                if (name == "empty?") {
                    type = JT.Bool;
                    return Ast.Call(JitRuntime.M("IsEmpty"), recv, Ast.Constant(arrays), Ast.Constant(hashes));
                }
                type = JT.Int;
                return Ast.Call(JitRuntime.M("SizeOf"), recv, Ast.Constant(arrays), Ast.Constant(hashes));
            }

            JT keyType;
            var key = Emit(args[0], out keyType);
            if (keyType == JT.Never || keyType == JT.None) { throw Bail(name + " of a value that jumps away"); }
            if (name == "fetch") {
                // Array#fetch (the library method) as well as Hash#fetch: `@list.fetch(i)' on a receiver
                // the profile cannot see would otherwise deopt on every call
                bool fetchArrays = IsPlainLibraryMethod(ArrayClass, "fetch");
                bool fetchHashes = IsPreludeHashFetch() && KeyLookupIsPlain();
                if (!fetchArrays && !fetchHashes) { throw Bail("fetch is neither Array's nor the prelude's"); }
                RequireCollectionReceiver(node.Target, fetchArrays, fetchHashes, name);
                type = JT.Obj;
                bool intKey = (keyType == JT.Int);
                var fetchKey = intKey ? key : Coerce(key, keyType, JT.Obj);
                if (args.Length == 1) {
                    return Ast.Call(JitRuntime.M(intKey ? "FetchInt" : "FetchObj"), recv, fetchKey,
                        Ast.Constant(fetchArrays), Ast.Constant(fetchHashes));
                }
                // the arguments are evaluated before the call, the default included
                JT defaultType;
                var defaultValue = Emit(args[1], out defaultType);
                if (defaultType == JT.Never || defaultType == JT.None) { throw Bail("fetch of a default that jumps away"); }
                return Ast.Call(JitRuntime.M(intKey ? "FetchIntDefault" : "FetchObjDefault"), recv, fetchKey,
                    Coerce(defaultValue, defaultType, JT.Obj), Ast.Constant(fetchArrays), Ast.Constant(fetchHashes));
            }
            if (!IsPlainLibraryMethod(HashClass, name) || !KeyLookupIsPlain()) {
                throw Bail(name + " is not the built-in one");
            }
            RequireCollectionReceiver(node.Target, false, true, name);

            type = JT.Bool;
            return (keyType == JT.Int)
                ? Ast.Call(JitRuntime.M("HashKeyInt"), recv, key)
                : Ast.Call(JitRuntime.M("HashKeyObj"), recv, Coerce(key, keyType, JT.Obj));
        }

        /// <summary>
        /// `recv[index] = v' and `recv[index] op= v' on an exact Array (an index in range) or an exact
        /// Hash that is not frozen, by the helper, which logs what it replaces (JitUndoLog). Ruby's
        /// order: the receiver, the index, (the element,) the value, the store; the expression's value
        /// is the value stored. An element read as an operand of op is speculated to be an Integer, or
        /// next to a Float an Integer or a Float.
        /// </summary>
        private MSA.Expression/*!*/ EmitIndexWrite(ArrayItemAccess/*!*/ element, string op, RExpr/*!*/ right, out JT type) {
            var args = element.Arguments.Expressions;
            if (element.Block != null || args.Length != 1 || args[0] is SplattedArgument) { throw Bail("[]= with " + args.Length + " arguments"); }
            if (op != null && !JitScreen.IsBinaryOperator(op)) { throw Bail("[] " + op + "="); }
            JT recvType, indexType;
            var recv = Emit(element.Array, out recvType);
            if (recvType != JT.Obj) { throw Bail("[]= on a " + recvType); }
            var index = Emit(args[0], out indexType);
            string suffix;
            switch (indexType) {
                case JT.Int: suffix = "Int"; break;
                case JT.Lng: suffix = "Long"; break;
                case JT.Obj: suffix = "Obj"; break;
                default: throw Bail("[]= with a " + indexType + " index");
            }
            bool arrays, hashes;
            RequireElementMethod("[]=", null, out arrays, out hashes);
            RequireCollectionReceiver(element.Array, arrays, hashes, "[]=");

            var recvTemp = Ast.Variable(typeof(object), "#recv");
            var indexTemp = Ast.Variable(ClrType(indexType), "#index");
            JT valueType;
            MSA.Expression value;
            if (op == null) {
                value = Emit(right, out valueType);
            } else {
                bool readArrays, readHashes;
                RequireElementMethod("[]", "default", out readArrays, out readHashes);
                arrays &= readArrays;
                hashes &= readHashes;
                if (!arrays && !hashes) { throw Bail("[] and []= are not the built-in ones"); }
                MSA.Expression current = Ast.Call(JitRuntime.M("Index" + suffix), recvTemp, indexTemp, Ast.Constant(arrays), Ast.Constant(hashes));
                JT rhsType;
                var rhs = Emit(right, out rhsType);
                JT currentType;
                if (rhsType == JT.Dbl) {
                    // Integer op Float and Float op Float are both Float op Float
                    RequireBuiltin(JT.Int, op);
                    current = Ast.Call(JitRuntime.M("SpeculateDouble"), current);
                    currentType = JT.Dbl;
                } else if (IsInteger(rhsType)) {
                    current = Ast.Call(JitRuntime.M("SpeculateLong"), current);
                    currentType = JT.Lng;
                } else {
                    throw Bail("[] " + op + "= of a " + rhsType);
                }
                value = EmitBinary(op, current, currentType, rhs, rhsType, out valueType);
            }
            if (valueType == JT.Never || valueType == JT.None) { throw Bail("[]= of a value that jumps away"); }
            var valueTemp = Ast.Variable(ClrType(valueType), "#value");
            _canDeopt = true;
            _hashWrite = true;
            if (_undoLog == null) {
                // in loop mode the entry sets both up; in a method the body reads the log on entry
                _undoLog = Ast.Variable(typeof(JitUndoLog), "#undo");
                _undoMark = Ast.Variable(typeof(int), "#undo-mark");
            }
            type = valueType;
            var store = (indexType == JT.Obj)
                ? Ast.Call(JitRuntime.M("IndexStoreObj"), Ast.Constant(_context), recvTemp, indexTemp, Coerce(valueTemp, valueType, JT.Obj),
                    Ast.Constant(arrays), Ast.Constant(hashes), _undoLog)
                : Ast.Call(JitRuntime.M("IndexStore" + suffix), recvTemp, indexTemp, Coerce(valueTemp, valueType, JT.Obj),
                    Ast.Constant(arrays), Ast.Constant(hashes), _undoLog);
            return Ast.Block(ClrType(valueType), new[] { recvTemp, indexTemp, valueTemp },
                Ast.Assign(recvTemp, recv),
                Ast.Assign(indexTemp, index),
                Ast.Assign(valueTemp, value),
                store,
                valueTemp
            );
        }
    }
}

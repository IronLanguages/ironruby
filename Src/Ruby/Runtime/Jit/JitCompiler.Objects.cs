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

        // ---- values only ever stored -------------------------------------------------------
        // `a[i] = v' stores the box v is: the generic code moves boxes around, so a v the body only
        // ever stores costs no allocation there. A v the body carries as a CLR int or double has to
        // be boxed again for each store - a new 24-byte object per element, which a collection
        // then keeps, so that filling a large Array ran 2x slower than the generic code for the
        // garbage collector's sake. A local (or a loop slot, or a parameter: JitScreen) that the
        // body reads only as the value it stores is therefore carried as an object from the start,
        // boxed where it is assigned, if it is assigned at all.

        private HashSet<string> _storedOnly;

        /// <summary>The body reads the local only as the value of an element or instance variable store.</summary>
        private bool IsStoredOnly(LocalVariable/*!*/ local) {
            if (_storedOnly == null) {
                var walker = new StoredOnlyWalker(_loop != null);
                if (_loop != null) {
                    walker.Walk(_loop);
                } else if (_ast.Body != null) {
                    walker.Walk(_ast.Body);
                }
                _storedOnly = walker.StoredOnly();
            }
            return _storedOnly.Contains(Key(local));
        }

        /// <summary>
        /// Collects the locals read only as `x[i] = v' or `@x = v' stores, by Key. Anything the walk does
        /// not see (a block, the values of a parallel assignment) is in a body Emit refuses anyway, and
        /// a local wrongly carried as an object is only slower, never wrong.
        /// </summary>
        private sealed class StoredOnlyWalker : Walker {
            private readonly bool _byDepth;
            private readonly HashSet<string>/*!*/ _stored = new HashSet<string>();
            private readonly HashSet<string>/*!*/ _other = new HashSet<string>();

            internal StoredOnlyWalker(bool byDepth) {
                _byDepth = byDepth;
            }

            private string/*!*/ Key(LocalVariable/*!*/ local) {
                return _byDepth ? local.Name + "@" + local.DefinitionLexicalDepth : local.Name;
            }

            internal HashSet<string>/*!*/ StoredOnly() {
                _stored.ExceptWith(_other);
                return _stored;
            }

            public override bool Enter(SimpleAssignmentExpression/*!*/ node) {
                var target = node.Left as LocalVariable;
                if (target != null) {
                    // an assignment is not a read - but `x op= v' reads x
                    if (node.Operation != null) { _other.Add(Key(target)); }
                    node.Right.Walk(this);
                    return false;
                }
                var stored = node.Right as LocalVariable;
                if (node.Operation == null && stored != null && (node.Left is ArrayItemAccess || node.Left is InstanceVariable)) {
                    node.Left.Walk(this);
                    _stored.Add(Key(stored));
                    return false;
                }
                return true;
            }

            public override bool Enter(LocalVariable/*!*/ node) {
                _other.Add(Key(node));
                return true;
            }
        }

        /// <summary>
        /// A value boxed to be stored: as Coerce boxes it, except that an int goes through the DLR's
        /// cache of small boxed ints, as the results of the generic code's Integer operations do.
        /// </summary>
        private MSA.Expression/*!*/ BoxForStore(MSA.Expression/*!*/ e, JT type) {
            if (type == JT.Int && !(e is MSA.ConstantExpression)) {
                return Ast.Call(typeof(Microsoft.Scripting.Runtime.ScriptingRuntimeHelpers).GetMethod("Int32ToObject"), e);
            }
            return Coerce(e, type, JT.Obj);
        }

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
                            Ast.Constant(FixnumUnequalKinds()))
                    );
                default:
                    throw Bail("when of an Integer on a " + subject.Type);
            }
        }

        /// <summary>
        /// The kinds (JitRuntime.CaseKind) of value v that Integer#== answers false for without calling Ruby
        /// code, as a mask: it asks `v == L', which nil, true, false and a Symbol answer by identity; a
        /// String, an Array and a Hash compare only with an object that responds to to_str, to_ary or
        /// to_hash, which an Integer does not; and a Range only with a Range. Each where v's class's ==
        /// is the library one - for a String, an Array, a Hash or a Range also on every singleton class.
        /// </summary>
        private int FixnumUnequalKinds() {
            int mask = 0;
            if (PlainEquals()) {
                mask |= (1 << JitRuntime.KindNil) | (1 << JitRuntime.KindTrue) | (1 << JitRuntime.KindFalse) | (1 << JitRuntime.KindSymbol);
            }
            var integer = KindClass(JitRuntime.KindFixnum);
            if (IsPlainLibraryMethod(KindClass(JitRuntime.KindString), "==") && NoToStr(integer)) {
                mask |= 1 << JitRuntime.KindString;
            }
            if (IsPlainLibraryMethod(KindClass(JitRuntime.KindArray), "==") && NoConversion(integer, "to_ary")) {
                mask |= 1 << JitRuntime.KindArray;
            }
            if (IsPlainLibraryMethod(KindClass(JitRuntime.KindHash), "==") && NoConversion(integer, "to_hash")) {
                mask |= 1 << JitRuntime.KindHash;
            }
            if (IsPlainLibraryMethod(KindClass(JitRuntime.KindRange), "==")) {
                mask |= 1 << JitRuntime.KindRange;
            }
            return mask;
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
            return NoConversion(cls, "to_str");
        }

        /// <summary>Instances of cls answer respond_to?(name) false without calling Ruby code.</summary>
        private bool NoConversion(RubyClass/*!*/ cls, string/*!*/ name) {
            return IsBuiltinMethod(cls, "respond_to?") && IsBuiltinMethod(cls, "respond_to_missing?")
                && !cls.ResolveMethod(name, VisibilityContext.AllVisible).Found;
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
                RequireNumericObservation(objectNode, op);
            }
            MSA.Expression number;
            JT numberType;
            if (other == JT.Dbl && IsComparison(op)) {
                // An Integer object compares with the Float exactly (EmitIntegerFloatCompare), so it
                // is not made a double first: JitRuntime.CompareNumberDouble orders either kind.
                if (leftObject) { RequireBuiltin(JT.Int, op); }
                RequireBuiltin(JT.Dbl, op);
                _canDeopt = true;
                if (leftObject) {
                    return OrderTest(op, Ast.Call(JitRuntime.M("CompareNumberDouble"), l, r), out type);
                }
                // the Float is evaluated first, as Ruby evaluates the receiver first
                var receiver = Ast.Variable(typeof(double), "#float");
                var test = OrderTest(Mirror(op), Ast.Call(JitRuntime.M("CompareNumberDouble"), r, receiver), out type);
                return Ast.Block(typeof(bool), new[] { receiver }, Ast.Assign(receiver, l), test);
            }
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

        /// <summary>
        /// `a[i]' in an outlined loop, a a local holding an Array whose elements, sampled, are all Integers:
        /// worth speculating to be an Integer even next to another object (a deopt tells otherwise).
        /// </summary>
        private bool HoldsIntegerElements(RExpr/*!*/ operand) {
            var read = operand as MethodCall;
            if (read == null || read.MethodName != "[]" || read.Block != null || read.Arguments == null ||
                read.Arguments.Expressions.Length != 1 || !(read.Target is LocalVariable) || _loop == null || _peekLocal == null) {
                return false;
            }
            var peek = _peekLocal((LocalVariable)read.Target);
            var array = peek.HasValue ? peek.Value.Value as RubyArray : null;
            if (array == null || array.Count == 0) { return false; }
            const int Samples = 16;
            int step = Math.Max(1, array.Count / Samples);
            for (int i = 0; i < array.Count; i += step) {
                object element = array[i];
                if (!(element is int) && !(element is long)) { return false; }
            }
            return true;
        }

        /// <summary>An operand the profile has only ever seen as something other than a number is not speculated to be one.</summary>
        private void RequireNumericObservation(RExpr/*!*/ operand, string/*!*/ op) {
            Type t = ObservedType(operand);
            if (t != null && t != MixedTypes && t != typeof(int) && t != typeof(long) && t != typeof(double)) {
                throw Bail("operator " + op + " on a " + ((t == typeof(void)) ? "nil" : t.Name));
            }
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
            // `name' is called with an explicit receiver, so it has to be public; `hashToo' is Hash#[]'s own call
            arrays = IsPlainLibraryMethod(ArrayClass, name) && IsPublic(ArrayClass, name);
            hashes = IsPlainLibraryMethod(HashClass, name) && IsPublic(HashClass, name)
                && (hashToo == null || IsPlainLibraryMethod(HashClass, hashToo)) && KeyLookupIsPlain();
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
        /// Hash#fetch is the library method (IDictionaryOps.Fetch), which looks the key up in the table
        /// itself: for fetch(k) the value or a KeyError, for fetch(k, d) the value or d.
        /// </summary>
        private bool IsLibraryHashFetch() {
            var hash = HashClass;
            if (!IsPlainLibraryMethod(hash, "fetch")) {
                return false;
            }
            var resolved = hash.ResolveMethod("fetch", VisibilityContext.AllVisible, _refinements);
            var library = resolved.Found ? resolved.Info as RubyLibraryMethodInfo : null;
            if (library == null) { return false; }
            foreach (var member in library.GetMembers()) {
                if (member.Name != "Fetch" || member.DeclaringType == null || member.DeclaringType.FullName != "IronRuby.Builtins.IDictionaryOps") {
                    return false;
                }
            }
            return true;
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
                bool fetchArrays = IsPlainLibraryMethod(ArrayClass, "fetch") && IsPublic(ArrayClass, "fetch");
                bool fetchHashes = IsLibraryHashFetch() && IsPublic(HashClass, "fetch") && KeyLookupIsPlain();
                if (!fetchArrays && !fetchHashes) { throw Bail("fetch is neither Array's nor Hash's library method"); }
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
            if (!IsPlainLibraryMethod(HashClass, name) || !IsPublic(HashClass, name) || !KeyLookupIsPlain()) {
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
            if (ArrayMethodIsPlain(true) && (op == null || ArrayMethodIsPlain(false))) {
                var array = ArrayLocal(element.Array);
                if (array != null) { return EmitArrayLocalWrite(array.Value, args[0], op, right, out type); }
            }
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
                ? Ast.Call(JitRuntime.M("IndexStoreObj"), Ast.Constant(_context), recvTemp, indexTemp, BoxForStore(valueTemp, valueType),
                    Ast.Constant(arrays), Ast.Constant(hashes), _undoLog)
                : Ast.Call(JitRuntime.M("IndexStore" + suffix), recvTemp, indexTemp, BoxForStore(valueTemp, valueType),
                    Ast.Constant(arrays), Ast.Constant(hashes), _undoLog);
            return Ast.Block(ClrType(valueType), new[] { recvTemp, indexTemp, valueTemp },
                Ast.Assign(recvTemp, recv),
                Ast.Assign(indexTemp, index),
                Ast.Assign(valueTemp, value),
                store,
                valueTemp
            );
        }

        // ---- Arrays an outlined loop loads once ----------------------------------------------
        // A renderer's inner loop - `fb[o + x] = cmap[pixels[(ty * 64) + tx]]' - indexes Arrays held in
        // locals the loop never writes, so each is the same object for the whole run. The entry loads
        // such a local once and checks that it holds a plain Array (JitRuntime.OsrArray): its exact
        // CLR type, with Array itself as its class - neither a subclass nor a singleton class of its
        // own. The global method version vouches for Array#[] and #[]= from there, and the body reads
        // and writes the array directly, the elements unboxed to the type a sample of them has through
        // a guard. Only the Array the entry loaded is checked, so a singleton #[] on some other Array
        // does not keep this one generic. Stores log what they replace in the shared JitUndoLog, as
        // EmitIndexWrite's do. Anything else - a local the loop writes, one holding something else -
        // goes through the intrinsics above.

        // the locals loaded once, by Key: the RubyArray variable and the type its elements are read as
        private Dictionary<string, KeyValuePair<MSA.ParameterExpression, JT>> _arrayLocals;
        // what the entry loads into those variables (JitLoopFacts.Arrays)
        private List<KeyValuePair<MSA.ParameterExpression, MSA.Expression>> _arrayLoads;
        // the locals themselves, which the loop then must not write (checked by TryCompileLoop)
        private List<LocalVariable> _arrayPeeked;
        // the second attempt at a loop that writes a local the first one loaded once
        private bool _noArrayLocals;
        private bool? _arrayGetPlain, _arraySetPlain;

        /// <summary>Array#[] (or #[]=) is the public library method, which a load-once Array answers by.</summary>
        private bool ArrayMethodIsPlain(bool store) {
            if ((_loop != null && _peekLocal == null) || _noArrayLocals || !_refinements.IsEmpty) { return false; }
            bool? ok = store ? _arraySetPlain : _arrayGetPlain;
            if (ok == null) {
                string name = store ? "[]=" : "[]";
                ok = IsBuiltinMethod(ArrayClass, name) && IsPublic(ArrayClass, name);
                if (store) { _arraySetPlain = ok; } else { _arrayGetPlain = ok; }
            }
            return ok.Value;
        }

        /// <summary>
        /// The RubyArray variable the local <paramref name="receiver"/> is loaded into, and the type its
        /// elements are read as; null when it is not a local holding a plain Array right now.
        /// </summary>
        private KeyValuePair<MSA.ParameterExpression, JT>? ArrayLocal(RExpr/*!*/ receiver) {
            var local = receiver as LocalVariable;
            if (local == null || _noArrayLocals || !_refinements.IsEmpty) { return null; }
            string key = Key(local);
            KeyValuePair<MSA.ParameterExpression, JT> known;
            if (_arrayLocals != null && _arrayLocals.TryGetValue(key, out known)) { return known; }
            if (_loop == null) { return ArrayParameter(local); }
            if (_peekLocal == null) { return null; }

            // bound to a slot already: the loop may write it
            MSA.ParameterExpression p;
            JT t;
            if (TryLookup(key, out p, out t)) { return null; }

            var peek = _peekLocal(local);
            if (!peek.HasValue) { return null; }
            var live = peek.Value.Value as RubyArray;
            RubyClass arrayClass = ArrayClass;
            if (live == null || live.GetType() != typeof(RubyArray) || _context.GetImmediateClassOf(live) != arrayClass) { return null; }

            var variable = Ast.Variable(typeof(RubyArray), "#array:" + local.Name);
            if (_arrayLocals == null) {
                _arrayLocals = new Dictionary<string, KeyValuePair<MSA.ParameterExpression, JT>>();
                _arrayLoads = new List<KeyValuePair<MSA.ParameterExpression, MSA.Expression>>();
                _arrayPeeked = new List<LocalVariable>();
            }
            _arrayLoads.Add(new KeyValuePair<MSA.ParameterExpression, MSA.Expression>(variable,
                Ast.Call(JitRuntime.M("OsrArray"), Ast.Convert(peek.Value.Key, typeof(object)), Ast.Constant(arrayClass, typeof(RubyClass)))));
            _arrayPeeked.Add(local);
            var result = new KeyValuePair<MSA.ParameterExpression, JT>(variable, ElementType(live));
            _arrayLocals.Add(key, result);
            return result;
        }

        // Method mode: the parameters loaded once, which the body then must not assign (checked by TryCompile).
        private List<MSA.ParameterExpression> _arrayParameters;

        /// <summary>
        /// Method mode: a parameter the profile has only seen hold an Array (JitStub's hints) is loaded
        /// the same way, once, at the top of the body; one that does not hold a plain Array this call
        /// deopts there, before the body has done anything. There is no element type to sample, so its
        /// elements are read as objects.
        /// </summary>
        private KeyValuePair<MSA.ParameterExpression, JT>? ArrayParameter(LocalVariable/*!*/ local) {
            if (_hints == null || _hints.Parameters == null || local.DefinitionLexicalDepth != _scopeDepth) { return null; }
            int i = Array.IndexOf(_paramNames, local.Name);
            if (i < 0 || i >= _hints.Parameters.Length || _hints.Parameters[i] != typeof(RubyArray)) { return null; }
            MSA.ParameterExpression p;
            JT t;
            if (!TryLookup(local.Name, out p, out t) || p != _paramExprs[i] || p.Type != typeof(object)) { return null; }

            var variable = Ast.Variable(typeof(RubyArray), "#array:" + local.Name);
            if (_arrayLocals == null) {
                _arrayLocals = new Dictionary<string, KeyValuePair<MSA.ParameterExpression, JT>>();
                _arrayLoads = new List<KeyValuePair<MSA.ParameterExpression, MSA.Expression>>();
                _arrayParameters = new List<MSA.ParameterExpression>();
            }
            _arrayLoads.Add(new KeyValuePair<MSA.ParameterExpression, MSA.Expression>(variable,
                Ast.Call(JitRuntime.M("MethodArray"), p, Ast.Constant(ArrayClass, typeof(RubyClass)))));
            _arrayParameters.Add(p);
            _canDeopt = true;
            var result = new KeyValuePair<MSA.ParameterExpression, JT>(variable, JT.Obj);
            _arrayLocals.Add(local.Name, result);
            return result;
        }

        /// <summary>Whether <paramref name="body"/> assigns one of <paramref name="parameters"/>.</summary>
        private static bool Assigns(MSA.Expression/*!*/ body, List<MSA.ParameterExpression>/*!*/ parameters) {
            var finder = new AssignmentFinder(parameters);
            finder.Visit(body);
            return finder.Found;
        }

        private sealed class AssignmentFinder : MSA.ExpressionVisitor {
            private readonly List<MSA.ParameterExpression>/*!*/ _parameters;
            internal bool Found;

            internal AssignmentFinder(List<MSA.ParameterExpression>/*!*/ parameters) {
                _parameters = parameters;
            }

            protected override MSA.Expression VisitBinary(MSA.BinaryExpression/*!*/ node) {
                var target = node.Left as MSA.ParameterExpression;
                if (node.NodeType == MSA.ExpressionType.Assign && target != null && _parameters.Contains(target)) { Found = true; }
                return base.VisitBinary(node);
            }

            protected override MSA.Expression VisitUnary(MSA.UnaryExpression/*!*/ node) {
                var target = node.Operand as MSA.ParameterExpression;
                if (target != null && _parameters.Contains(target)) {
                    switch (node.NodeType) {
                        case MSA.ExpressionType.PreIncrementAssign:
                        case MSA.ExpressionType.PreDecrementAssign:
                        case MSA.ExpressionType.PostIncrementAssign:
                        case MSA.ExpressionType.PostDecrementAssign:
                            Found = true;
                            break;
                    }
                }
                return base.VisitUnary(node);
            }

            protected override MSA.Expression VisitExtension(MSA.Expression/*!*/ node) {
                // a node of the DLR's own reduces to the standard ones
                return node.CanReduce ? Visit(node.Reduce()) : node;
            }
        }

        /// <summary>
        /// What an Array's elements are, from a sample of them: Int or Dbl when every one sampled is
        /// a Fixnum, or every one a Float - a typed read is then unboxed through a guard that deopts on
        /// anything else - and Obj otherwise.
        /// </summary>
        private static JT ElementType(RubyArray/*!*/ array) {
            const int Samples = 16;
            int count = array.Count;
            if (count == 0) { return JT.Obj; }
            int step = Math.Max(1, count / Samples);
            JT kind = JT.None;
            for (int i = 0; i < count; i += step) {
                object element = array[i];
                JT k = (element is int) ? JT.Int : (element is double) ? JT.Dbl : JT.Obj;
                if (k == JT.Obj || (kind != JT.None && kind != k)) { return JT.Obj; }
                kind = k;
            }
            return kind;
        }

        /// <summary>An index into a load-once Array: an Integer that fits in an Int32. Anything else deopts, or bails.</summary>
        private MSA.Expression/*!*/ EmitArrayIndex(RExpr/*!*/ node) {
            JT t;
            var e = Emit(node, out t);
            e = NumericIVar(node, e, ref t);
            switch (t) {
                case JT.Int: return e;
                case JT.Lng: return Coerce(e, JT.Lng, JT.Int);
                case JT.Obj: _canDeopt = true; return Ast.Call(JitRuntime.M("GuardInt"), e);
                default: throw Bail("Array index of type " + t);
            }
        }

        /// <summary>`a[i]' of a load-once Array: the element, unboxed when <paramref name="typed"/> and the elements have one type.</summary>
        private MSA.Expression/*!*/ EmitArrayRead(KeyValuePair<MSA.ParameterExpression, JT> array, RExpr/*!*/ index, bool typed, out JT type) {
            MSA.Expression read = Ast.Call(JitRuntime.M("ArrayGet"), array.Key, EmitArrayIndex(index));
            if (!typed || array.Value == JT.Obj) {
                type = JT.Obj;
                return read;
            }
            _canDeopt = true;
            type = array.Value;
            return Ast.Call(JitRuntime.M(array.Value == JT.Int ? "GuardInt" : "GuardDouble"), read);
        }

        /// <summary>`a[i]' where a is a load-once Array and Array#[] the library method; null otherwise.</summary>
        private KeyValuePair<MSA.ParameterExpression, JT>? ArrayLocalRead(RExpr/*!*/ node) {
            var call = node as MethodCall;
            if (call == null || call.MethodName != "[]" || call.Block != null || call.Arguments == null ||
                call.Arguments.Expressions.Length != 1 || call.Arguments.Expressions[0] is SplattedArgument ||
                !(call.Target is LocalVariable) || !ArrayMethodIsPlain(false)) {
                return null;
            }
            return ArrayLocal(call.Target);
        }

        /// <summary>
        /// `a[i] = v', `a[i] op= v' on a load-once Array: EmitIndexWrite's store, of the array itself.
        /// `a[i] = b[j]' moves the element as the object it is, never unboxed and boxed again.
        /// </summary>
        private MSA.Expression/*!*/ EmitArrayLocalWrite(KeyValuePair<MSA.ParameterExpression, JT> array, RExpr/*!*/ indexNode, string op,
            RExpr/*!*/ right, out JT type) {
            var indexTemp = Ast.Variable(typeof(int), "#index");
            var index = EmitArrayIndex(indexNode);
            JT valueType;
            MSA.Expression value;
            if (op == null) {
                var from = ArrayLocalRead(right);
                if (from != null) {
                    value = EmitArrayRead(from.Value, ((MethodCall)right).Arguments.Expressions[0], false, out valueType);
                } else {
                    value = Emit(right, out valueType);
                }
            } else {
                MSA.Expression current = Ast.Call(JitRuntime.M("ArrayGet"), array.Key, indexTemp);
                JT rhsType;
                var rhs = Emit(right, out rhsType);
                JT currentType;
                if (array.Value == JT.Int && IsInteger(rhsType)) {
                    current = Ast.Call(JitRuntime.M("GuardInt"), current);
                    currentType = JT.Int;
                } else if (array.Value == JT.Dbl && rhsType == JT.Dbl) {
                    current = Ast.Call(JitRuntime.M("GuardDouble"), current);
                    currentType = JT.Dbl;
                } else if (rhsType == JT.Dbl) {
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
                _undoLog = Ast.Variable(typeof(JitUndoLog), "#undo");
                _undoMark = Ast.Variable(typeof(int), "#undo-mark");
            }
            type = valueType;
            return Ast.Block(ClrType(valueType), new[] { indexTemp, valueTemp },
                Ast.Assign(indexTemp, index),
                Ast.Assign(valueTemp, value),
                Ast.Call(JitRuntime.M("ArrayStore"), array.Key, indexTemp, BoxForStore(valueTemp, valueType), _undoLog),
                valueTemp
            );
        }
    }
}

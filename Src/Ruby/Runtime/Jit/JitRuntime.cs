/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * Runtime support: the deoptimization signal, the guarded arithmetic helpers the
 * specialized code calls, and the reflection handles the compiler emits.
 *
 * ***************************************************************************/

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;

    /// <summary>
    /// Thrown by specialized code when an assumption it was compiled under no longer holds
    /// (Fixnum arithmetic overflowing into Bignum, a division by zero that has to raise).
    ///
    /// A specialized body is only ever produced for a *pure* method - one that cannot be
    /// observed part way through - so the entry stub answers a deopt by re-running the generic
    /// body from the top. That is why there is no mid-body state materialization here: the
    /// compiler refuses to specialize any body for which restarting would be visible.
    /// </summary>
    [Serializable]
    internal sealed class JitDeoptException : Exception {
        internal static readonly JitDeoptException Instance = new JitDeoptException();
        private JitDeoptException() : base("IronRuby JIT deoptimization") { }
    }

    /// <summary>
    /// Runtime helpers called from JIT-compiled code.
    /// </summary>
    public static partial class JitRuntime {
        /// <summary>IR_JIT_VERBOSE=1 traces what gets specialized and what is rejected.</summary>
        internal static readonly bool Verbose = Environment.GetEnvironmentVariable("IR_JIT_VERBOSE") == "1";

        /// <summary>
        /// Statistics, printed by -X:JITStats.
        /// </summary>
        internal static int Specialized, Rejected, Deopts, Invalidations, ScreenRejected, DeoptRetired, DeoptRetried;
        internal static long CompileTicks;

        /// <summary>
        /// Which node kinds the static pre-screen turned methods away on. Only collected under
        /// IR_JIT_VERBOSE: it is how a JitCompiler.Emit case the screen has not been told about
        /// becomes visible - the newly supported node kind sits at the top of this histogram.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, int> _screenReasons =
            Verbose ? new System.Collections.Generic.Dictionary<string, int>() : null;

        /// <summary>A method the pre-screen decided could never compile, so it was never wrapped.</summary>
        internal static void RecordScreenRejection(string nodeKind) {
            ScreenRejected++;
            if (_screenReasons == null) { return; }
            lock (_screenReasons) {
                int n;
                _screenReasons.TryGetValue(nodeKind ?? "?", out n);
                _screenReasons[nodeKind ?? "?"] = n + 1;
            }
        }

        private static int _statsHooked;

        /// <summary>With IR_JIT_VERBOSE=1, print what the JIT did on the way out.</summary>
        internal static void HookStats() {
            if (!Verbose || System.Threading.Interlocked.Exchange(ref _statsHooked, 1) != 0) { return; }
            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                Console.Error.WriteLine("[jit] specialized={0} rejected={1} screen-rejected={2} deopts={3} invalidations={4} retired={5} retried={6} compile={7:F1}ms",
                    Specialized, Rejected, ScreenRejected, Deopts, Invalidations, DeoptRetired, DeoptRetried,
                    CompileTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                PrintScreenReasons();
            };
        }

        /// <summary>
        /// The pre-screen's histogram, most frequent first. A node kind JitCompiler.Emit has
        /// since learned to compile showing up here is the screen having fallen behind it.
        /// </summary>
        private static void PrintScreenReasons() {
            if (_screenReasons == null || _screenReasons.Count == 0) { return; }
            var reasons = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(_screenReasons);
            reasons.Sort((a, b) => b.Value.CompareTo(a.Value));
            var text = new System.Text.StringBuilder("[jit] screen rejected:");
            for (int i = 0; i < reasons.Count && i < 8; i++) {
                text.AppendFormat(" {0}={1}", reasons[i].Key, reasons[i].Value);
            }
            Console.Error.WriteLine(text.ToString());
        }

        // ---- integer arithmetic with overflow deopt -------------------------------------
        // An Integer is an Int32 while it fits in one, an Int64 while it fits in that, and a
        // BigInteger beyond. The specialized code carries the first two unboxed, so all of its
        // integer arithmetic happens in long: two ints can be added, subtracted or multiplied
        // in 64 bits with no check at all, and only an operation with a long operand needs one.
        //
        // Overflow out of Int64 deopts rather than producing a BigInteger. Producing one would
        // make the static type of every arithmetic result `object', which is to say no
        // specialization at all; the deopt is one predictable branch per operation and happens
        // at most once per specialized region.

        // The int-in, int-out shapes. `a + b' on two ints is a long, but where it is stored
        // straight back into an int local the whole thing is one operation with one check, and
        // the compiler fuses it back into these rather than widen and narrow around a check.

        public static int AddInt(int a, int b) {
            long r = (long)a + b;
            int i = (int)r;
            if (i != r) { throw Deopt(); }
            return i;
        }

        public static int SubInt(int a, int b) {
            long r = (long)a - b;
            int i = (int)r;
            if (i != r) { throw Deopt(); }
            return i;
        }

        public static int MulInt(int a, int b) {
            long r = (long)a * b;
            int i = (int)r;
            if (i != r) { throw Deopt(); }
            return i;
        }

        /// <summary>Ruby Integer#/ is floor division and raises on a zero divisor.</summary>
        public static int DivInt(int a, int b) {
            if (b == 0 || (a == Int32.MinValue && b == -1)) { throw Deopt(); }
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) { q--; }
            return q;
        }

        /// <summary>Ruby Integer#% takes the sign of the divisor.</summary>
        public static int ModInt(int a, int b) {
            if (b == 0 || (a == Int32.MinValue && b == -1)) { throw Deopt(); }
            int m = a % b;
            if (m != 0 && ((m < 0) != (b < 0))) { m += b; }
            return m;
        }

        public static long AddLong(long a, long b) {
            long r = unchecked(a + b);
            // Signed overflow iff both operands differ in sign from the result.
            if (((a ^ r) & (b ^ r)) < 0) { throw Deopt(); }
            return r;
        }

        public static long SubLong(long a, long b) {
            long r = unchecked(a - b);
            // Signed overflow iff the operands differ in sign and the result differs from a.
            if (((a ^ b) & (a ^ r)) < 0) { throw Deopt(); }
            return r;
        }

        public static long MulLong(long a, long b) {
            long low;
            long high = Math.BigMul(a, b, out low);
            // The 128-bit product fits in 64 bits iff the high half is the sign extension.
            if (high != (low >> 63)) { throw Deopt(); }
            return low;
        }

        /// <summary>Ruby Integer#/ is floor division and raises on a zero divisor.</summary>
        public static long DivLong(long a, long b) {
            if (b == 0 || (a == Int64.MinValue && b == -1)) { throw Deopt(); }
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) { q--; }
            return q;
        }

        /// <summary>Ruby Integer#% takes the sign of the divisor.</summary>
        public static long ModLong(long a, long b) {
            if (b == 0 || (a == Int64.MinValue && b == -1)) { throw Deopt(); }
            long m = a % b;
            if (m != 0 && ((m < 0) != (b < 0))) { m += b; }
            return m;
        }

        /// <summary>
        /// A typed body's value as the object its entry returns: an Int32 or a Boolean comes out of
        /// the shared boxes (the DLR's cache of small Integers, the two Booleans) rather than a new
        /// box per call, the way every other place in the runtime makes one.
        /// </summary>
        internal static MSA.Expression/*!*/ BoxResult(MSA.Expression/*!*/ value) {
            if (value.Type == typeof(int)) {
                return Ast.Call(typeof(ScriptingRuntimeHelpers).GetMethod("Int32ToObject", new[] { typeof(int) }), value);
            }
            if (value.Type == typeof(bool)) {
                return Ast.Call(typeof(ScriptingRuntimeHelpers).GetMethod("BooleanToObject", new[] { typeof(bool) }), value);
            }
            return Ast.Convert(value, typeof(object));
        }

        /// <summary>
        /// The one representation rule, as the specialized code sees it: a value that fits in an
        /// Int32 IS an Int32, boxed from the small-integer cache. Every long that leaves the
        /// specialized region - a return value, a store back into a locals tuple, an ivar write -
        /// goes through here, which is what keeps #hash, #eql?, #equal? and Hash keys right for a
        /// value the JIT produced. Mirrors ClrInteger.Narrow, which lives in the other assembly.
        /// </summary>
        public static object/*!*/ NarrowLong(long v) {
            return (v >= Int32.MinValue && v <= Int32.MaxValue)
                ? ScriptingRuntimeHelpers.Int32ToObject((Int32)v) : (object)v;
        }

        /// <summary>Is this boxed value one the unboxed long representation can carry?</summary>
        public static bool IsIntegral(object value) {
            return value is int || value is long;
        }

        public static long ToLong(object value) {
            return (value is int) ? (long)(int)value : (long)value;
        }

        /// <summary>
        /// A long flowing into a slot the specialization typed as int - an int local, an int
        /// parameter of a self-call. It only fits when the value fits, and if it does not the
        /// Ruby value has genuinely grown past Int32: deopt and let the generic body carry it.
        /// </summary>
        public static int DemoteToInt(long v) {
            if (v < Int32.MinValue || v > Int32.MaxValue) { throw Deopt(); }
            return (int)v;
        }

        public static double DivDouble(double a, double b) {
            return a / b;
        }

        /// <summary>Ruby Float#% takes the sign of the divisor (IEEE remainder does not).</summary>
        public static double ModDouble(double a, double b) {
            double m = a % b;
            if (m != 0.0 && ((m < 0.0) != (b < 0.0))) { m += b; }
            return m;
        }

        public static double PowDouble(double a, double b) {
            return Math.Pow(a, b);
        }

        internal static Exception Deopt() {
            Deopts++;
            _deoptThrown = System.Diagnostics.Stopwatch.GetTimestamp();
            return JitDeoptException.Instance;
        }

        // When this thread last threw a deopt, for the method entry that catches it to know what
        // the exception cost (EntryDeopted). Zero once taken.
        [ThreadStatic]
        private static long _deoptThrown;

        // ---- safe points -----------------------------------------------------------------

        /// <summary>
        /// The back-edge safe point of an outlined (OSR) loop. A specialized copy keeps the Ruby
        /// locals in CLR locals, so it must not let a Thread#raise escape from the middle of
        /// itself - the tuples would be left holding the values from the loop's entry. Instead
        /// it hands the loop back at the iteration boundary, through the deopt path that already
        /// stores the locals home, and the generic loop's own safe point (RubyUtils.SafePoint,
        /// first thing on its back edge) delivers the exception. OsrLoopSite.Run does not count
        /// this as a failed specialization.
        ///
        /// For a JIT-specialized *method* body use RubyUtils.SafePoint instead: a deopt there
        /// re-runs the whole method generically, and an interrupt can simply be thrown.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OsrSafePoint() {
            if (RubyUtils.IsSafePointRequested) {
                throw SafePointDeopt();
            }
        }

        /// <summary>
        /// A loop's safe point in a specialized method. What is waiting runs here, and the body goes on -
        /// unless <paramref name="deopt"/> says the body writes Hash or Array elements: a trap handler
        /// run in the middle of those could change an element a later deopt takes back to what it was
        /// before the handler ran. Such a body deopts instead (it is one that may: JitCompiler takes no
        /// impure body that can deopt), and the generic body runs what is waiting at its first safe point.
        /// </summary>
        public static void MethodSafePoint(StrongBox<bool>/*!*/ deopt) {
            if (RubyUtils.IsSafePointRequested) {
                MethodSafePointSlow(deopt);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void MethodSafePointSlow(StrongBox<bool>/*!*/ deopt) {
            if (deopt.Value) { throw SafePointDeopt(); }
            RubyUtils.SafePoint();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception/*!*/ SafePointDeopt() {
            return JitDeoptException.Instance;
        }

        // ---- guards ----------------------------------------------------------------------

        /// <summary>
        /// The entry guard: the global method version has not moved since specialization. Any
        /// def/undef/alias/include/singleton-method anywhere bumps RubyModule's global method
        /// version, and so does a method coming or going in a refinement (it re-versions the
        /// classes below the refined module), so this one check covers method redefinition,
        /// overriding in a subclass, singleton methods and refinements.
        /// </summary>
        public static bool VersionOk(int snapshot) {
            return RubyModule.GlobalMethodVersion == snapshot;
        }

        /// <summary>A method entry's VersionOk failed: it is stale for good (see JitStub.VersionChanged).</summary>
        public static void EntryStale(object/*!*/ specialization) {
            var spec = (JitStub.Specialization)specialization;
            spec.Stub.VersionChanged(spec);
        }

        /// <summary>A method entry's typed body deopted (see JitStub.OnDeopt).</summary>
        public static void EntryDeopted(object/*!*/ specialization) {
            long caught = System.Diagnostics.Stopwatch.GetTimestamp();
            long thrown = _deoptThrown;
            _deoptThrown = 0;
            var spec = (JitStub.Specialization)specialization;
            spec.Stub.OnDeopt(spec, (thrown != 0) ? caught - thrown : -1);
        }

        /// <summary>An outlined loop was compiled under an older method version; build it again.</summary>
        public static object OsrVersionStale() {
            if (Osr.OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   guard failed: method version"); }
            return Osr.OsrLoopSite.Retype;
        }

        /// <summary>An entry guard of an outlined loop did not hold; hand the loop back.</summary>
        public static object OsrGuardFailed(string/*!*/ what) {
            if (Osr.OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   guard failed: {0}", what); }
            return Osr.OsrLoopSite.Retry;
        }

        /// <summary>
        /// A local does not hold the CLR type the copy was built for. That is a different answer
        /// from a plain guard failure: the usual reason is that an accumulator grew out of Int32
        /// and is now a long, so a copy built over the types it holds *now* would run. The site
        /// rebuilds one, a bounded number of times.
        /// </summary>
        public static object OsrTypeGuardFailed(string/*!*/ what) {
            if (Osr.OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   type guard failed: {0}", what); }
            return Osr.OsrLoopSite.Retype;
        }

        /// <summary>A loop read a local it has not written in this run: it holds nil.</summary>
        public static Exception/*!*/ OsrUnsetSlot() {
            return Deopt();
        }

        // ---- keyword parameters (see JitKeywords) ---------------------------------------

        /// <summary>The entry guard of a method with keywords: the prologue would bind them without raising.</summary>
        public static bool KeywordsMatch(object/*!*/ keywords, object kw) {
            return ((JitKeywords)keywords).Matches(kw);
        }

        /// <summary>The value the prologue binds keyword number <paramref name="index"/> to.</summary>
        public static object KeywordValue(object/*!*/ keywords, object kw, int index) {
            return ((JitKeywords)keywords).Value(kw, index);
        }

        // KeywordsMatch and KeywordValue in one call, for a method with up to three keywords.

        public static bool KeywordsRead1(object/*!*/ keywords, object kw, out object v0) {
            return ((JitKeywords)keywords).Read(kw, out v0);
        }

        public static bool KeywordsRead2(object/*!*/ keywords, object kw, out object v0, out object v1) {
            return ((JitKeywords)keywords).Read(kw, out v0, out v1);
        }

        public static bool KeywordsRead3(object/*!*/ keywords, object kw, out object v0, out object v1, out object v2) {
            return ((JitKeywords)keywords).Read(kw, out v0, out v1, out v2);
        }

        public static RubyClass/*!*/ ClassOf(RubyContext/*!*/ context, object self) {
            IRubyObject obj = self as IRubyObject;
            return obj != null ? obj.ImmediateClass : context.GetImmediateClassOf(self);
        }

        // ---- instance variables without a scope ------------------------------------------

        // Each `@x` in a compiled body has an inline cache of its own: the same shape-checked fast
        // path as the interpreter's and the DLR compiler's (see RubyInstanceData).
        public static object GetIVar(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            return RubyOps.ReadInstanceVariable(context, self, site);
        }

        /// <summary>
        /// `x', `self.x' or `o.x' in an outlined loop where x was, on the receiver, an attr_reader when
        /// the loop was compiled (JitCompiler.EmitCall): one per such call. Or `o.x = v' where x= was
        /// an attr_writer (JitCompiler.EmitAttributeWrite), if <see cref="Writer"/>.
        /// </summary>
        public sealed class SelfAttrSite {
            internal readonly string/*!*/ Name;
            internal readonly string/*!*/ VariableName;
            internal readonly InstanceVariableSite/*!*/ Variable;
            // `o.x' with an explicit receiver other than self can only call a public x.
            internal readonly bool PublicOnly;
            internal readonly bool Writer;

            // The class the reader was last found on, under which global method version. Replaced
            // as a whole, so a racing reader sees one consistent pair.
            private sealed class Binding {
                internal readonly RubyClass/*!*/ Class;
                internal readonly int Version;
                internal Binding(RubyClass/*!*/ cls, int version) { Class = cls; Version = version; }
            }
            private volatile Binding _bound;

            internal SelfAttrSite(string/*!*/ name, string/*!*/ variableName, bool publicOnly)
                : this(name, variableName, publicOnly, false) {
            }

            internal SelfAttrSite(string/*!*/ name, string/*!*/ variableName, bool publicOnly, bool writer) {
                Name = name;
                PublicOnly = publicOnly;
                Writer = writer;
                VariableName = variableName;
                Variable = new InstanceVariableSite(variableName);
            }

            internal bool IsBoundTo(RubyClass/*!*/ cls) {
                var b = _bound;
                return b != null && b.Class == cls && b.Version == RubyModule.GlobalMethodVersion;
            }

            /// <summary>Whether Name is, on cls, the attr_reader (attr_writer) of VariableName.</summary>
            internal bool Bind(RubyClass/*!*/ cls) {
                int version = RubyModule.GlobalMethodVersion;
                if ((Writer ? WriterVariable(cls, Name, PublicOnly) : ReaderVariable(cls, Name, PublicOnly)) != VariableName) { return false; }
                _bound = new Binding(cls, version);
                return true;
            }

            /// <summary>
            /// The instance variable `name' reads on cls, if it is an attr_reader there (a public one,
            /// if <paramref name="publicOnly"/>).
            /// </summary>
            internal static string ReaderVariable(RubyClass/*!*/ cls, string/*!*/ name, bool publicOnly) {
                var resolved = cls.ResolveMethod(name, IronRuby.Runtime.Calls.VisibilityContext.AllVisible);
                if (!resolved.Found || (publicOnly && !resolved.Info.IsPublic)) { return null; }
                var reader = resolved.Info as IronRuby.Runtime.Calls.RubyAttributeReaderInfo;
                return (reader != null) ? reader.InstanceVariableName : TrivialAccessorVariable(cls, resolved.Info, false);
            }

            /// <summary>
            /// The instance variable `name' writes on cls, if it is an attr_writer there (or a Ruby method
            /// that is one in all but name, see TrivialAccessorVariable).
            /// </summary>
            internal static string WriterVariable(RubyClass/*!*/ cls, string/*!*/ name, bool publicOnly) {
                var resolved = cls.ResolveMethod(name, IronRuby.Runtime.Calls.VisibilityContext.AllVisible);
                if (!resolved.Found || (publicOnly && !resolved.Info.IsPublic)) { return null; }
                var writer = resolved.Info as IronRuby.Runtime.Calls.RubyAttributeWriterInfo;
                return (writer != null) ? writer.InstanceVariableName : TrivialAccessorVariable(cls, resolved.Info, true);
            }

            /// <summary>
            /// `def x; @x; end' reads @x and `def x=(v); @x = v; end' (by whatever names) writes it, as
            /// attr_reader and attr_writer do, and nothing a call of either does is observable but that
            /// - not even in a backtrace, since a read cannot raise and a write that would deopts to the
            /// real call first. So an outlined loop reads or writes such a method's variable as it would
            /// an attribute's, as TruffleRuby inlines trivial methods. Null if the method is not one.
            /// </summary>
            private static string TrivialAccessorVariable(RubyClass/*!*/ cls, IronRuby.Runtime.Calls.RubyMemberInfo/*!*/ info, bool writer) {
                var method = info as IronRuby.Runtime.Calls.RubyMethodInfo;
                // nor under coverage: the call counts the method and its line, which a read of the variable would not
                if (method == null || cls.Context.RubyOptions.EnableTracing || cls.Context.RubyOptions.Profile ||
                    method.Body.Coverage != null) { return null; }

                var ast = method.GetSyntaxTree();
                var parameters = ast.Parameters;
                int arity = writer ? 1 : 0;
                if (parameters.Mandatory.Length != arity || parameters.Optional.Length != 0 || parameters.Unsplat != null ||
                    parameters.Block != null || parameters.SuperSplat != null) {
                    return null;
                }
                var signature = parameters.Signature;
                if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords ||
                    signature.MinPositionalCount != arity || signature.MaxPositionalCount != arity)) {
                    return null;
                }

                var body = ast.Body;
                if (body == null || body.RescueClauses != null && body.RescueClauses.Count != 0 || body.ElseStatements != null ||
                    body.EnsureStatements != null || body.Statements.Count != 1) {
                    return null;
                }

                var statement = body.Statements.First;
                if (!writer) {
                    var read = statement as IronRuby.Compiler.Ast.InstanceVariable;
                    return (read != null) ? read.Name : null;
                }

                var assignment = statement as IronRuby.Compiler.Ast.SimpleAssignmentExpression;
                var parameter = parameters.Mandatory[0] as IronRuby.Compiler.Ast.LocalVariable;
                var target = (assignment != null && assignment.Operation == null) ? assignment.Left as IronRuby.Compiler.Ast.InstanceVariable : null;
                var value = (target != null) ? assignment.Right as IronRuby.Compiler.Ast.LocalVariable : null;
                return (parameter != null && value != null && value.Name == parameter.Name &&
                    value.DefinitionLexicalDepth == parameter.DefinitionLexicalDepth) ? target.Name : null;
            }
        }

        /// <summary>
        /// The read an attr_reader does, as long as the method `site' names is still that reader on
        /// self's class: re-checked against the class and the global method version on every read,
        /// so a redefinition, a singleton method or a different self deopts to the generic loop,
        /// which then makes the call. A read is not observable, so the deopt is invisible.
        /// </summary>
        public static object ReadSelfAttr(RubyContext/*!*/ context, object self, SelfAttrSite/*!*/ site) {
            var obj = self as RubyObject;
            RubyClass cls = (obj != null) ? obj.ImmediateClass : ClassOf(context, self);
            if (!site.IsBoundTo(cls) && !site.Bind(cls)) {
                throw Deopt();
            }
            if (obj != null) {
                // RubyOps.ReadInstanceVariable's fast path, the RubyObject test already made
                var data = obj._instanceData;
                if (data == null) { return null; }
                object[] slots = data._ivars;
                var entry = site.Variable.Cache;
                if (RubyInstanceData.ShapeOf(slots) == entry.Shape) {
                    return RubyInstanceData.GetSlot(slots, entry.Index);
                }
                return data.GetInstanceVariable(site.Variable);
            }
            return RubyOps.ReadInstanceVariable(context, self, site.Variable);
        }

        /// <summary>
        /// The write an attr_writer does, under the same checks as ReadSelfAttr's, of a variable the
        /// object already has: the slot's old value goes into the undo log, so a deopt later in the
        /// iteration takes the write back (JitUndoLog.Rollback) and the restart is invisible. Anything
        /// else - a frozen object (whose FrozenError needs the real call), a variable to add (a shape
        /// change), a full log, an object that is not a RubyObject - deopts before writing.
        /// </summary>
        public static void WriteSelfAttr(object self, object value, SelfAttrSite/*!*/ site, JitUndoLog/*!*/ log) {
            var obj = self as RubyObject;
            if (obj == null || (!site.IsBoundTo(obj.ImmediateClass) && !site.Bind(obj.ImmediateClass))) {
                throw Deopt();
            }
            var data = obj._instanceData;
            if (data == null || data._frozen || log.IsFull) {
                throw Deopt();
            }
            object[] slots = data._ivars;
            var shape = RubyInstanceData.ShapeOf(slots);
            var variable = site.Variable;
            var entry = variable.Cache;
            int index;
            if (shape == entry.Shape && entry.Next == null) {
                index = entry.Index;
            } else if (shape == entry.OtherShape) {
                index = entry.OtherIndex;
            } else {
                var current = (InstanceVariableShape)shape;
                index = current.IsSentinel ? 0 : current.IndexOf(variable.Name);
                if (index == 0) {
                    throw Deopt();
                }
                variable.Update(entry.Remember(current, index));
            }
            object old = RubyInstanceData.GetSlot(slots, index);
            RubyInstanceData.SetSlot(slots, index, value);
            if (RubyInstanceData.ShapeOf(slots) != shape) {
                // the slots moved under the write (another thread added or removed a variable):
                // make the write again where they are now; the variable exists, so it is a store
                data.SetInstanceVariable(variable.Name, value);
            }
            log.Replaced(data, variable.Name, old);
        }

        public static object SetIVar(RubyContext/*!*/ context, object self, object value, InstanceVariableSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj != null) {
                var data = obj._instanceData ?? RubyOps.GetInstanceData(ref obj._instanceData);
                if (!data._frozen && data.TrySetCached(site.Cache, value, obj)) {
                    return value;
                }
            } else if (RubyOps.TrySetRubyObjectInstanceVariable(self, value, site)) {
                // an instance of a Ruby subclass of Array, Hash, Exception, Enumerator...
                return value;
            }
            // A frozen receiver raises FrozenError, and a typed body has no frame to raise it from:
            // let the generic body run instead (see JitCompiler._ivarWrite).
            if (context.IsObjectFrozen(self)) {
                throw Deopt();
            }
            context.SetInstanceVariable(self, value, site);
            return value;
        }

        /// <summary>
        /// The opening run of instance variable writes of a specialized method (JitCompiler.
        /// EmitInitialIVars): the shape a fresh object has after all of them.
        /// </summary>
        public sealed class InitialIVarsSite {
            internal readonly InstanceVariableShape/*!*/ Shape;

            private InitialIVarsSite(InstanceVariableShape/*!*/ shape) {
                Shape = shape;
            }

            /// <summary>Null if the variables would make an object too complex for shapes.</summary>
            internal static InitialIVarsSite TryCreate(string/*!*/[]/*!*/ names) {
                var shape = InstanceVariableShape.Root;
                foreach (var name in names) {
                    shape = shape.GetChild(name);
                    if (shape == null) { return null; }
                }
                return new InitialIVarsSite(shape);
            }
        }

        /// <summary>
        /// The slot array a fresh object would get the run's variables in - element 0 already the
        /// final shape, the values to be stored by the caller - or null if self is not a RubyObject
        /// without instance variables, or is frozen: then the writes are made one by one.
        /// </summary>
        public static object[] BeginInitialIVars(object self, InitialIVarsSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj == null) { return null; }
            var data = obj._instanceData;
            if (data != null && (data._frozen || data._ivars != InstanceVariableShape.EmptySlots)) { return null; }
            int count = site.Shape.Count;
            var result = new object[Math.Max(count, obj.ImmediateClass.InstanceVariableCapacity) + 1];
            result[0] = site.Shape;
            return result;
        }

        /// <summary>
        /// Publishes the array BeginInitialIVars made and the caller filled. False if the object
        /// got variables, or was frozen, in the meantime: the caller then makes the writes one by one.
        /// </summary>
        public static bool CommitInitialIVars(object self, object[]/*!*/ slots) {
            var obj = (RubyObject)self;
            var data = obj._instanceData;
            if (data == null) {
                var fresh = new RubyInstanceData();
                fresh._ivars = slots;
                if (System.Threading.Interlocked.CompareExchange(ref obj._instanceData, fresh, null) == null) {
                    RubyInstanceData.UpdateCapacityHint(obj.ImmediateClass, ((InstanceVariableShape)slots[0]).Count);
                    return true;
                }
                data = obj._instanceData;
            }
            if (data._frozen) { return false; }
            if (System.Threading.Interlocked.CompareExchange(ref data._ivars, slots, InstanceVariableShape.EmptySlots) == InstanceVariableShape.EmptySlots) {
                RubyInstanceData.UpdateCapacityHint(obj.ImmediateClass, ((InstanceVariableShape)slots[0]).Count);
                return true;
            }
            return false;
        }

        // An instance variable used as a numeric operand of an outlined loop (JitCompiler.NumericIVar):
        // unboxed while it holds the type it held when the loop was compiled, a deopt once it does not.

        public static int GuardInt(object value) {
            if (value is int) { return (int)value; }
            throw Deopt();
        }

        public static long GuardLong(object value) {
            if (value is int) { return (int)value; }
            if (value is long) { return (long)value; }
            throw Deopt();
        }

        public static double GuardDouble(object value) {
            if (value is double) { return (double)value; }
            throw Deopt();
        }

        // ---- Float#to_i (JitCompiler.EmitCall) -------------------------------------------

        /// <summary>
        /// Float#to_i of a Float whose Integer fits in an Int64; a NaN, an infinity (FloatDomainError)
        /// or a Bignum is the generic loop's to produce.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FloatToLong(double value) {
            // -2^63 <= value < 2^63, and false for NaN
            if (value >= -9223372036854775808.0 && value < 9223372036854775808.0) { return (long)value; }
            throw Deopt();
        }

        // ---- reflection handles the compiler emits ---------------------------------------

        internal static MethodInfo/*!*/ M(string/*!*/ name) {
            return typeof(JitRuntime).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        }
    }
}

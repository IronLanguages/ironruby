/* ****************************************************************************
 *
 * -X:OSR for the loops the core iterators run: `n.times { }', `a.upto(b) { }',
 * `a.downto(b) { }', and Array's `each', `map', `select', `any?', `all?' and `none?'.
 *
 * Those loops are C# loops in the library that yield to a block, so there is no `while' for
 * OsrLoopSite to replace, and every iteration pays for a block invocation and a dynamic call
 * site per operator in the body. TruffleRuby gets out of that by inlining the block into the
 * iterator's loop. This does the same thing in the shape OSR already knows how to specialize:
 * once a block has been yielded to often enough by one of these iterators, it is handed to
 * JitLoopCompiler as the `while' loop the iterator and the block make together:
 *
 *     n.times { |i| body }           while #i < #n;     i = #i;      body; #i += 1; end
 *     a.upto(b) { |i| body }         while #i <= #n;    i = #i;      body; #i += 1; end
 *     a.downto(b) { |i| body }       while #i >= #n;    i = #i;      body; #i -= 1; end
 *     array.each { |x| body }        while #i < #a.size; x = #a[#i]; body; #i += 1; end
 *
 * and for map, select and any? the block's value - the last statement of the body - goes into
 * `#r[#i] = value', `if value; #r[#k] = #a[#i]; #k += 1; end' and `if value; #k = 1; break; end'
 * (`unless' for all?), #r a result Array the iterator lends the copy, cut back afterwards to
 * what the iterations it ran produced.
 *
 * The iterator runs the rest of its iterations in the typed copy. The counter lives in a
 * tuple of the block's own lexical depth, made for the run, next to the block's parameter; the
 * outer locals the body reads and writes are reached through the tuples of the scopes the block
 * was defined in, exactly the ones the block's compiled body reaches them through.
 *
 * The iterator is still the method that is running (its frame is in a backtrace, a redefinition
 * of Integer#times never gets here) and the copy only exists for bodies whose iterations cannot
 * be observed part way through, so handing the loop back is as invisible as it is for a `while':
 * a deopt restores the locals to the top of the iteration, the counter says which one that is,
 * and the iterator yields from there.
 *
 * What the screen below keeps out is what a `while' and a block do differently: the jumps
 * (`break' leaves the iterator with a value, `next' ends the iteration, `redo' restarts it, a
 * `return' leaves the method), and block-local variables, which are fresh - nil - on every
 * iteration, where a loop's locals would carry over. The block's single plain parameter is
 * assigned at the top of every iteration, and so is a local whose first mention is a statement
 * of the body that assigns it (`x = i * 2; s += x'); a block with any other local is not
 * outlined.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Runtime.Osr {
    public enum BlockLoopKind {
        Times = 0,
        UpTo = 1,
        DownTo = 2,
        Each = 3,
        Map = 4,
        Select = 5,
        // any?, none?: stops at the first element the block is truthy for; all?: at the first falsy one
        FindTruthy = 6,
        FindFalsy = 7,
    }

    /// <summary>
    /// What a block literal needs to be outlined into an iterator's loop: one per block literal
    /// that passed the screen, on its BlockDispatcher, and the sites for each iterator it is given to.
    /// </summary>
    internal sealed class BlockLoopInfo {
        private const int KindCount = 8;

        internal readonly BlockDefinition/*!*/ Ast;
        internal readonly RubyContext/*!*/ Context;
        internal readonly int Depth;
        // the block's one parameter, or null if it has none
        internal readonly LocalVariable Parameter;
        // the block's other locals, each assigned by a statement of the body before anything reads it
        internal readonly LocalVariable/*!*/[]/*!*/ Locals;
        internal readonly string/*!*/ Name;
        internal readonly BlockLoopSite[]/*!*/ Sites = new BlockLoopSite[KindCount];

        private BlockLoopInfo(BlockDefinition/*!*/ ast, RubyContext/*!*/ context, int depth, LocalVariable parameter,
            LocalVariable/*!*/[]/*!*/ locals, string/*!*/ name) {
            Ast = ast;
            Locals = locals;
            Context = context;
            Depth = depth;
            Parameter = parameter;
            Name = name;
        }

        /// <summary>
        /// Null when the block can never be outlined: anything but at most one plain parameter, or a
        /// body with a jump, a block-local variable it may read before it assigns it, or a block of its own.
        /// </summary>
        internal static BlockLoopInfo TryCreate(BlockDefinition/*!*/ ast, RubyContext/*!*/ context, int depth, string/*!*/ name) {
            if (depth <= 0) { return null; }

            var parameters = ast.Parameters;
            if (parameters.Optional.Length != 0 || parameters.Unsplat != null || parameters.Block != null ||
                parameters.SuperSplat != null || parameters.Mandatory.Length > 1) {
                return null;
            }
            var signature = parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords || signature.MaxPositionalCount != parameters.Mandatory.Length)) {
                return null;
            }

            LocalVariable parameter = null;
            if (parameters.Mandatory.Length == 1) {
                parameter = parameters.Mandatory[0] as LocalVariable;
                if (parameter == null || parameter.DefinitionLexicalDepth != depth) {
                    return null;
                }
            }

            List<LocalVariable> locals;
            if (ast.Body.Count == 0 || !Screen.Accepts(ast.Body, depth, parameter, out locals)) {
                return null;
            }
            return new BlockLoopInfo(ast, context, depth, parameter, locals.ToArray(), name);
        }

        internal BlockLoopSite/*!*/ GetSite(BlockLoopKind kind) {
            var site = Sites[(int)kind];
            if (site == null) {
                Interlocked.CompareExchange(ref Sites[(int)kind], new BlockLoopSite(this, kind), null);
                site = Sites[(int)kind];
            }
            return site;
        }

        /// <summary>The block-level constructs a `while' would run differently.</summary>
        private sealed class Screen : Walker {
            private readonly int _depth;
            private readonly LocalVariable _parameter;
            private int _loops;
            private bool _rejected;

            private Screen(int depth, LocalVariable parameter) {
                _depth = depth;
                _parameter = parameter;
            }

            // The block's own locals other than its parameter: each the target of a statement of the
            // body's own, `x = v', before anything else in the body mentions it (see Enter(LocalVariable)).
            private readonly List<LocalVariable>/*!*/ _locals = new List<LocalVariable>();

            internal static bool Accepts(Statements/*!*/ body, int depth, LocalVariable parameter, out List<LocalVariable> locals) {
                var screen = new Screen(depth, parameter);
                locals = screen._locals;
                foreach (var statement in body) {
                    var assignment = statement as SimpleAssignmentExpression;
                    var target = (assignment != null && assignment.Operation == null) ? assignment.Left as LocalVariable : null;
                    if (target != null && target.DefinitionLexicalDepth == depth && !screen.IsKnown(target)) {
                        // the value first, which may not read the local it is about to be stored in
                        assignment.Right.Walk(screen);
                        screen._locals.Add(target);
                    } else {
                        statement.Walk(screen);
                    }
                    if (screen._rejected) { return false; }
                }
                return true;
            }

            private bool IsKnown(LocalVariable/*!*/ local) {
                if (_parameter != null && local.Name == _parameter.Name) { return true; }
                foreach (var known in _locals) {
                    if (known.Name == local.Name) { return true; }
                }
                return false;
            }

            private bool Reject() {
                _rejected = true;
                return false;
            }

            public override bool Enter(WhileLoopExpression/*!*/ node) { _loops++; return !_rejected; }
            public override void Exit(WhileLoopExpression/*!*/ node) { _loops--; }

            // In a `while' of the body's own these jump within the body; at the block's level they
            // leave the iteration or the iterator.
            public override bool Enter(BreakStatement/*!*/ node) { return _loops > 0 ? !_rejected : Reject(); }
            public override bool Enter(NextStatement/*!*/ node) { return _loops > 0 ? !_rejected : Reject(); }
            public override bool Enter(RedoStatement/*!*/ node) { return _loops > 0 ? !_rejected : Reject(); }
            public override bool Enter(RetryStatement/*!*/ node) { return Reject(); }
            public override bool Enter(ReturnStatement/*!*/ node) { return Reject(); }
            public override bool Enter(YieldCall/*!*/ node) { return Reject(); }
            public override bool Enter(SuperCall/*!*/ node) { return Reject(); }
            public override bool Enter(BlockDefinition/*!*/ node) { return Reject(); }
            public override bool Enter(BlockReference/*!*/ node) { return Reject(); }

            // The Walker does not go into the block of a call (`a.each { }', `m(&b)'), so the two above
            // never see one: catch it at the call. The loop emitter cannot take a call with a block either
            // (JitCompiler: "call with a block"), so without this a block like `n.times { a.each { } }' was
            // let through, and the first entry spent a whole OSR compile (~25ms) only to retire the site.
            public override bool Enter(MethodCall/*!*/ node) { return node.Block != null ? Reject() : !_rejected; }

            public override bool Enter(LocalVariable/*!*/ node) {
                // A local of the block's own is nil at the top of every iteration, which a loop slot
                // carried over from the iteration before is not. The parameter is assigned at the top of
                // every iteration, and so is a local whose first mention is a statement that assigns it;
                // any other is out.
                if (node.DefinitionLexicalDepth < 0 || (node.DefinitionLexicalDepth >= _depth &&
                    (node.DefinitionLexicalDepth != _depth || !IsKnown(node)))) {
                    return Reject();
                }
                return !_rejected;
            }
        }
    }

    /// <summary>
    /// One block literal given to one kind of iterator: the synthesized loop, and the OsrLoopSite
    /// that counts, compiles, deopts and retires it exactly as it would a `while'.
    /// </summary>
    public sealed class BlockLoopSite {
        /// <summary>
        /// Iterations an entry must have left for the copy to be worth entering: the entry allocates
        /// the argument array and a tuple and runs the copy's guards, which a handful of yields beat.
        /// </summary>
        public const int MinRun = 16;

        private readonly BlockLoopInfo/*!*/ _info;
        private readonly BlockLoopKind _kind;
        private readonly OsrLoopSite/*!*/ _osr;

        // the tuple of the block's own depth: its locals, then the counter, the limit (or the Array),
        // the result Array of a map or select, and how many elements a select has kept
        private readonly int _tupleSize, _counterIndex, _limitIndex, _resultIndex, _keptIndex;
        private readonly Type/*!*/ _tupleType;

        internal BlockLoopSite(BlockLoopInfo/*!*/ info, BlockLoopKind kind) {
            _info = info;
            _kind = kind;
            _osr = new OsrLoopSite(info.Name + " (" + kind.ToString().ToLowerInvariant() + ")");

            // the block's locals keep the slots its body gives them, the counter and the limit go after
            int first = (info.Parameter != null) ? info.Parameter.ClosureIndex + 1 : 0;
            bool compiled = info.Parameter == null || info.Parameter.ClosureIndex >= 0;
            foreach (var local in info.Locals) {
                first = Math.Max(first, local.ClosureIndex + 1);
                compiled &= local.ClosureIndex >= 0;
            }
            _counterIndex = first;
            _limitIndex = first + 1;
            _resultIndex = first + 2;
            _keptIndex = first + 3;
            _tupleSize = first + 4;
            var types = new Type[_tupleSize];
            for (int i = 0; i < types.Length; i++) { types[i] = typeof(object); }
            _tupleType = MutableTuple.MakeTupleType(types);

            if (!compiled) {
                // the block has not been compiled the way this expects: never enter
                Retire();
                return;
            }

            int depth = info.Depth;
            var tupleArgIndex = new Dictionary<int, int>();
            for (int d = depth; d >= 0; d--) {
                tupleArgIndex.Add(d, depth - d);
            }
            _osr.LoopAst = MakeLoop(info, kind, depth, _counterIndex, _limitIndex, _resultIndex, _keptIndex);
            _osr.Context = info.Context;
            _osr.ScopeDepth = depth;
            _osr.TupleArgIndex = tupleArgIndex;
            _osr.SelfArgIndex = depth + 1;
            _osr.ScopeArgIndex = depth + 2;
        }

        private void Retire() {
            _osr.Counting = false;
            _osr.Countdown = Int64.MaxValue;
        }

        private static LocalVariable/*!*/ Hidden(string/*!*/ name, int depth, int index, SourceSpan location) {
            var local = new LocalVariable(name, location, depth);
            local.SetClosureIndex(index);
            return local;
        }

        private static WhileLoopExpression/*!*/ MakeLoop(BlockLoopInfo/*!*/ info, BlockLoopKind kind, int depth,
            int counterIndex, int limitIndex, int resultIndex, int keptIndex) {
            SourceSpan location = info.Ast.Location;
            var counter = Hidden("#i", depth, counterIndex, location);
            var limit = Hidden("#n", depth, limitIndex, location);
            var result = Hidden("#r", depth, resultIndex, location);
            var kept = Hidden("#k", depth, keptIndex, location);

            Expression condition;
            Expression element;
            switch (kind) {
                case BlockLoopKind.Times:
                    condition = new MethodCall(counter, "<", new Arguments(limit), location);
                    element = counter;
                    break;
                case BlockLoopKind.UpTo:
                    condition = new MethodCall(counter, "<=", new Arguments(limit), location);
                    element = counter;
                    break;
                case BlockLoopKind.DownTo:
                    condition = new MethodCall(counter, ">=", new Arguments(limit), location);
                    element = counter;
                    break;
                case BlockLoopKind.Each:
                case BlockLoopKind.Map:
                case BlockLoopKind.Select:
                case BlockLoopKind.FindTruthy:
                case BlockLoopKind.FindFalsy:
                    condition = new MethodCall(counter, "<", new Arguments(new MethodCall(limit, "size", null, location)), location);
                    element = new MethodCall(limit, "[]", new Arguments(counter), location);
                    break;
                default:
                    throw new ArgumentException("kind");
            }

            var statements = new Statements();
            if (info.Parameter != null) {
                statements.Add(new SimpleAssignmentExpression(info.Parameter, element, null, location));
            }
            var body = info.Ast.Body;
            int last = body.Count - 1;
            for (int i = 0; i < last; i++) {
                statements.Add(body[i]);
            }
            switch (kind) {
                case BlockLoopKind.Map:
                    // #r[#i] = <the block's value>, #r the result, which Run has made as long as the receiver
                    statements.Add(new SimpleAssignmentExpression(
                        new ArrayItemAccess(result, new Arguments(counter), null, location), body[last], null, location));
                    break;
                case BlockLoopKind.Select:
                    // if <the block's value>; #r[#k] = #a[#i]; #k += 1; end - the element as it is once
                    // the block has run, as Select's own loop adds it
                    var keep = new Statements();
                    keep.Add(new SimpleAssignmentExpression(new ArrayItemAccess(result, new Arguments(kept), null, location),
                        new MethodCall(limit, "[]", new Arguments(counter), location), null, location));
                    keep.Add(new SimpleAssignmentExpression(kept, Literal.Integer(1, location), "+", location));
                    statements.Add(new IfExpression(body[last], keep, new List<ElseIfClause>(), location));
                    break;
                case BlockLoopKind.FindTruthy:
                case BlockLoopKind.FindFalsy:
                    // if <the block's value>; #k = 1; break; end - the counter stays at the element found
                    var found = new Statements();
                    found.Add(new SimpleAssignmentExpression(kept, Literal.Integer(1, location), null, location));
                    found.Add(new BreakStatement(null, location));
                    statements.Add(kind == BlockLoopKind.FindTruthy
                        ? (Expression)new IfExpression(body[last], found, new List<ElseIfClause>(), location)
                        : new UnlessExpression(body[last], found, null, location));
                    break;
                default:
                    statements.Add(body[last]);
                    break;
            }
            statements.Add(new SimpleAssignmentExpression(counter, Literal.Integer(1, location),
                kind == BlockLoopKind.DownTo ? "-" : "+", location));

            return new WhileLoopExpression(condition, true, false, statements, location);
        }

        /// <summary>
        /// The site for this block given to an iterator of this kind, or null when the block can
        /// never be outlined. The iterator reads Countdown into a local on entry, counts its yields
        /// down with it, and calls Run when it reaches zero.
        /// </summary>
        public static BlockLoopSite For(BlockParam/*!*/ block, BlockLoopKind kind) {
            var proc = block.Proc;
            var info = proc.Dispatcher.LoopInfo;
            if (info == null || proc.Kind == ProcKind.Lambda) { return null; }
            return info.GetSite(kind);
        }

        /// <summary>Yields left before the next entry asks for the copy.</summary>
        public long Countdown {
            get { return _osr.Countdown; }
        }

        /// <summary>
        /// On the way out: what this entry did not spend, so that entries that leave early (a break, or a
        /// search that stops) still add up. A loop shorter than MinRun never gets here: its iterator does
        /// not ask for the site at all, which keeps a hot short loop (`a.each { }' over a handful of
        /// elements, nested in a long one) the code it was before.
        /// </summary>
        public void Leave(long countdown) {
            if (_osr.Counting && countdown < _osr.Countdown) {
                _osr.Countdown = countdown;
            }
        }

        /// <summary>
        /// Runs the counted iterations from <paramref name="i"/> on in the typed copy (Times, UpTo,
        /// DownTo) and answers the counter the iterator resumes at: the end of the range when the copy
        /// ran it all, the iteration it handed back otherwise, untouched if there was no copy to run.
        /// </summary>
        public int Run(BlockParam/*!*/ block, int i, int limit, ref long countdown) {
            Debug.Assert(_kind <= BlockLoopKind.DownTo);
            int kept;
            Run(block, i, ScriptingRuntimeHelpers.Int32ToObject(limit), null, 0, out i, out kept);
            countdown = _osr.Countdown;
            return i;
        }

        /// <summary>Array#each's: runs from index <paramref name="i"/> on, answers the index to resume at.</summary>
        public int Run(BlockParam/*!*/ block, RubyArray/*!*/ array, int i, ref long countdown) {
            Debug.Assert(_kind == BlockLoopKind.Each);
            int kept;
            Run(block, i, array, null, 0, out i, out kept);
            countdown = _osr.Countdown;
            return i;
        }

        /// <summary>
        /// Array#map's and #select's: runs from index <paramref name="i"/> on, the block's values going
        /// into <paramref name="result"/> (map: one per element, select: the elements kept), and answers
        /// the index to resume at, with <paramref name="result"/> holding exactly what the iterations
        /// before it produced.
        /// </summary>
        public int Run(BlockParam/*!*/ block, RubyArray/*!*/ array, RubyArray/*!*/ result, int i, ref long countdown) {
            Debug.Assert(_kind == BlockLoopKind.Map || _kind == BlockLoopKind.Select);
            // The copy stores into the result rather than appends to it, so it gets room for every
            // element there is, which no Ruby code sees before it is cut back to what was produced.
            int kept = result.Count;
            int room = array.Count - kept;
            if (room > 0) { result.AddMultiple(room, null); }
            Run(block, i, array, result, kept, out i, out kept);
            if (_kind == BlockLoopKind.Map) { kept = i; }
            if (result.Count > kept) { result.RemoveRange(kept, result.Count - kept); }
            countdown = _osr.Countdown;
            return i;
        }

        /// <summary>
        /// Array#any?, #all? and #none?'s: runs from index <paramref name="i"/> on and answers the index
        /// to resume at, or the index of the element the search stops at, which <paramref name="found"/>
        /// then says it is.
        /// </summary>
        public int Run(BlockParam/*!*/ block, RubyArray/*!*/ array, int i, ref long countdown, out bool found) {
            Debug.Assert(_kind == BlockLoopKind.FindTruthy || _kind == BlockLoopKind.FindFalsy);
            int kept;
            Run(block, i, array, null, 0, out i, out kept);
            found = (kept == 1);
            countdown = _osr.Countdown;
            return i;
        }

        private void Run(BlockParam/*!*/ block, int from, object limit, RubyArray result, int keptBefore, out int resume, out int kept) {
            var proc = block.Proc;
            RubyScope scope = proc.LocalScope;
            int depth = _info.Depth;

            var own = MutableTuple.MakeTuple(_tupleType, new object[_tupleSize]);
            own.SetNestedValue(_tupleSize, _counterIndex, ScriptingRuntimeHelpers.Int32ToObject(from));
            own.SetNestedValue(_tupleSize, _limitIndex, limit);
            own.SetNestedValue(_tupleSize, _resultIndex, result);
            own.SetNestedValue(_tupleSize, _keptIndex, ScriptingRuntimeHelpers.Int32ToObject(keptBefore));

            // the tuples by lexical depth, the block's own first, then the scopes it was defined in,
            // the same chain its compiled body walks with GetParentScope
            var args = new object[depth + 3];
            args[0] = own;
            RubyScope s = scope;
            for (int k = 1; k <= depth && s != null; k++) {
                args[k] = s.Locals;
                s = s.Parent;
            }
            args[depth + 1] = block.Self;
            args[depth + 2] = scope;

            // Whatever the copy did - ran to the end, handed the loop back, never ran - the counter in
            // the tuple is the iteration to go on from, and the locals are as of the top of it.
            _osr.Run(args);
            object next = own.GetNestedValue(_tupleSize, _counterIndex);
            resume = (next is int) ? (int)next : from;
            object keptAfter = own.GetNestedValue(_tupleSize, _keptIndex);
            kept = (keptAfter is int) ? (int)keptAfter : keptBefore;
        }
    }
}

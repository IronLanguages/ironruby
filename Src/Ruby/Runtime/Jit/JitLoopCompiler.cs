/* ****************************************************************************
 *
 * -X:OSR, second tier: a type-specialized copy of a hot loop.
 *
 * Outlining a loop and handing it to the DLR's compiler is, on its own, worth almost
 * nothing: IronRuby's compiled tier runs an arithmetic loop at the same speed as its
 * interpreter, because what costs is the dynamic call site on every `+' and every `<', not
 * the interpretation. What makes the outlining pay is that it gives the loop a compilation
 * point of its own - and at that point the loop's locals are sitting in the enclosing
 * scope's MutableTuple with their values visible, which is a type profile no counter had to
 * collect.
 *
 * So when a loop turns hot, this asks JitCompiler for a typed body, with the scope's locals
 * bound to CLR int/long/double/bool locals loaded from the tuple on entry and stored back on
 * the way out. Inside, an Integer add is an add. A long goes back into the tuple through the
 * representation funnel, so a value that fits in an Int32 arrives as one.
 *
 * Deoptimization is what keeps that honest. The body signals one (JitDeoptException) when an
 * Integer overflows out of Int64, when one that grew past Int32 has to go back into an int
 * local, when a division would raise, or when anything else it assumed stops holding. In the
 * second of those the loop is usually better off with a *new* copy rather than none, which is
 * what OsrLoopSite's Retype answer is for.
 *
 * The loop keeps a copy of its locals as they were at the top of the current
 * iteration; a deopt restores those and returns the Retry sentinel, so the loop is handed
 * back at an iteration boundary and the generic copy carries on from there. JitCompiler only
 * produces a body whose iterations are unobservable part way through, which is what makes
 * re-running the interrupted one invisible.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Compiler.Ast;
using IronRuby.Builtins;
using IronRuby.Runtime.Osr;
using Microsoft.Scripting;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;

    /// <summary>One scope local the loop touches, bound to its slot in the scope's tuple.</summary>
    internal sealed class JitLoopSlot {
        internal JT Type;
        internal MSA.ParameterExpression/*!*/ Variable;
        internal MSA.ParameterExpression/*!*/ Snapshot;
        /// <summary>The tuple property chain the entry reads and the exits write.</summary>
        internal MSA.Expression/*!*/ Accessor;
        /// <summary>
        /// Non-null when the slot did not hold a value of this type when the loop turned hot -
        /// a temporary the loop creates itself. The entry then loads nothing and this flag says
        /// whether the run has written the slot yet; the exits store it only once it has.
        /// </summary>
        internal MSA.ParameterExpression Assigned;
        internal MSA.ParameterExpression AssignedSnapshot;
    }

    internal static class JitLoopCompiler {
        /// <summary>The boxed form a slot's value goes home as.</summary>
        private static MSA.Expression/*!*/ Box(MSA.Expression/*!*/ value, bool isLong) {
            return isLong ? Ast.Call(JitRuntime.M("NarrowLong"), value) : (MSA.Expression)Ast.Convert(value, typeof(object));
        }

        /// <summary>
        /// Builds a typed entry for a loop, or returns null when the loop is not the shape
        /// this can specialize. <paramref name="args"/> are the locals tuples the site passes,
        /// one per lexical depth; they carry the live values the types are read from, which is
        /// a type profile a loop that is already running gets for free. The loop's self is
        /// args[<paramref name="selfArgIndex"/>], or there is none if that is negative; its scope
        /// is args[<paramref name="scopeArgIndex"/>].
        /// </summary>
        internal static Func<object[], object> TryCompile(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context,
            int scopeDepth, Dictionary<int, int>/*!*/ tupleArgIndex, object[]/*!*/ args, int selfArgIndex, int scopeArgIndex) {

            // The refinements active where the loop is written: the operators it inlines are resolved
            // under them, as its call sites would resolve them.
            RubyScope scope = (scopeArgIndex >= 0 && scopeArgIndex < args.Length) ? args[scopeArgIndex] as RubyScope : null;
            if (scope == null) { return null; }
            RefinementActivation refinements = scope.GetActiveRefinements();

            // What the prologue's version guard compares against: read before the compiler
            // resolves anything (see JitStub.TrySpecialize), so that a method table change racing
            // with the compile leaves the copy stale rather than right for good. The same for the
            // constants the body folds.
            int version = RubyModule.GlobalMethodVersion;
            int constantVersion = context.ConstantAccessVersion;

            var argsParameter = Ast.Parameter(typeof(object[]), "#args");
            var exit = Ast.Label(typeof(object), "#osr-jit-exit");
            var tuples = new Dictionary<int, MSA.ParameterExpression>();
            var tupleInit = new List<MSA.Expression>();
            var tupleGuards = new List<MSA.Expression>();

            // A local's tuple slot: the expression that addresses it in the copy, and what it holds
            // now. Null if the local's tuple was not passed.
            Func<LocalVariable, KeyValuePair<MSA.Expression, object>?> peek = local => {
                int depth = local.DefinitionLexicalDepth;
                int argIndex;
                if (!tupleArgIndex.TryGetValue(depth, out argIndex) || argIndex >= args.Length) {
                    if (OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   no tuple for {0} at depth {1}", local.Name, depth); }
                    return null;
                }
                object tupleValue = args[argIndex];
                if (tupleValue == null) { return null; }

                MSA.ParameterExpression tuple;
                if (!tuples.TryGetValue(depth, out tuple)) {
                    Type tupleType = tupleValue.GetType();
                    tuple = Ast.Parameter(tupleType, "#tuple" + depth);
                    tuples.Add(depth, tuple);
                    tupleGuards.Add(Ast.Not(Ast.TypeIs(Ast.ArrayIndex(argsParameter, Ast.Constant(argIndex)), tupleType)));
                    tupleInit.Add(Ast.Assign(tuple,
                        Ast.Convert(Ast.ArrayIndex(argsParameter, Ast.Constant(argIndex)), tupleType)));
                }

                try {
                    MSA.Expression accessor = ScopeBuilder.GetVariableAccessor(tuple, local.ClosureIndex);
                    // Read the live value down the very path the emitted code will use.
                    object value = tupleValue;
                    foreach (var property in MutableTuple.GetAccessPath(tupleValue.GetType(), local.ClosureIndex)) {
                        value = property.GetValue(value, null);
                    }
                    return new KeyValuePair<MSA.Expression, object>(accessor, value);
                } catch (Exception) {
                    return null;
                }
            };

            Func<LocalVariable, JT, JitLoopSlot> bind = (local, hint) => {
                var peeked = peek(local);
                if (!peeked.HasValue) { return null; }
                object current = peeked.Value.Value;
                MSA.Expression accessor = peeked.Value.Key;

                JT type;
                bool writeFirst = (hint == JT.Int || hint == JT.Lng || hint == JT.Dbl);
                if (!writeFirst && hint == JT.Obj) {
                    // Met first as the target of an object (an element read, a constant): an object
                    // slot, which holds whatever the tuple holds - nil, if the loop has not written it.
                    type = JT.Obj;
                } else if (writeFirst) {
                    // The loop met this local as the target of an assignment, so the type it
                    // will hold is the one being stored into it, whatever it holds right now.
                    // What it holds at entry is then not something to insist on either - the
                    // scope may be brand new and the slot nil - so the slot carries a flag
                    // rather than a guard: reads before the first write deopt, and the exits
                    // leave the slot alone until the flag is up.
                    type = hint;
                } else if (current is int) {
                    type = JT.Int;
                } else if (current is long) {
                    // An accumulator that has already outgrown an Int32. It is carried unboxed
                    // in a CLR long and narrows back on the way out.
                    type = JT.Lng;
                } else if (current is double) {
                    type = JT.Dbl;
                } else {
                    // Anything else - an Array, a Hash, nil, true - is carried as an object, which the
                    // emitter only moves around, tests for truth, or hands to a helper that checks it.
                    type = JT.Obj;
                }

                Type clr = (type == JT.Int) ? typeof(int) : (type == JT.Lng) ? typeof(long) : (type == JT.Dbl) ? typeof(double) : typeof(object);
                return new JitLoopSlot {
                    Type = type,
                    Variable = Ast.Parameter(clr, local.Name),
                    Snapshot = Ast.Parameter(clr, local.Name + "#snap"),
                    Accessor = accessor,
                    Assigned = writeFirst ? Ast.Parameter(typeof(bool), local.Name + "#set") : null,
                    AssignedSnapshot = writeFirst ? Ast.Parameter(typeof(bool), local.Name + "#set-snap") : null,
                };
            };

            // The loop's self, which its instance variable reads go through. The live object is a
            // profile as well: what one of its variables holds when the loop turns hot is the type
            // a numeric use of the variable is specialized for (JitCompiler.NumericIVar).
            MSA.ParameterExpression self = null;
            Func<string, JT> ivarProfile = null;
            Func<string, string> attrProfile = null;
            if (selfArgIndex >= 0 && selfArgIndex < args.Length) {
                object liveSelf = args[selfArgIndex];
                self = Ast.Parameter(typeof(object), "#self");
                ivarProfile = name => {
                    object value;
                    if (!context.TryGetInstanceVariable(liveSelf, name, out value)) { return JT.Obj; }
                    return (value is int) ? JT.Int : (value is long) ? JT.Lng : (value is double) ? JT.Dbl : JT.Obj;
                };
                // and which of self's methods are attr_readers, and of what (JitCompiler.EmitCall)
                RubyClass selfClass = JitRuntime.ClassOf(context, liveSelf);
                attrProfile = name => JitRuntime.SelfAttrSite.ReaderVariable(selfClass, name, false);
            }

            List<JitLoopSlot> slots;
            bool canDeopt;
            JitCompiler.JitLoopFacts facts;
            // what self is, as a hint for the Array and Hash intrinsics (JitCompiler.ObservedType)
            var hints = new JitCompiler.ReceiverHints();
            if (selfArgIndex >= 0 && selfArgIndex < args.Length) {
                hints.Self = (args[selfArgIndex] == null) ? typeof(void) : args[selfArgIndex].GetType();
            }
            MSA.Expression body = JitCompiler.TryCompileLoop(loop, context, scopeDepth, bind, self, ivarProfile, attrProfile, peek, scope, hints,
                refinements, out slots, out canDeopt, out facts);
            if (body == null || slots.Count == 0) {
                if (OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   emitter gave up (body={0} slots={1})", body != null, slots == null ? -1 : slots.Count); }
                return null;
            }

            // Bind self only if the body reads it (an instance variable, `self'): an unused #self
            // local loaded in the prologue cost the JIT's register allocation of the loop itself -
            // `while i < m; i += 1; end' ran 1.4x slower per iteration with it.
            if (self != null && !UsesParameter.In(body, self)) {
                self = null;
            }

            var locals = new List<MSA.ParameterExpression>(tuples.Values);
            var probe = Ast.Parameter(typeof(object), "#probe");
            locals.Add(probe);
            if (self != null) {
                locals.Add(self);
            }
            if (facts.UndoLog != null) {
                locals.Add(facts.UndoLog);
                locals.Add(facts.UndoMark);
            }

            // The scope this loop belongs to always makes tuples of the same shape, but nothing
            // in the type system says so: check before the cast rather than let an unexpected
            // one become an InvalidCastException in the middle of Ruby code.
            var prologue = new List<MSA.Expression>();
            foreach (var guard in tupleGuards) {
                prologue.Add(Ast.IfThen(guard, Ast.Return(exit, Ast.Constant(OsrLoopSite.Retry, typeof(object)))));
            }
            prologue.AddRange(tupleInit);

            // The same loop can be entered under other refinements than the ones the copy was built
            // under - an eval string's, say: build one for those. Not a Retype: the locals' types are
            // as the copy expects, and those rebuilds are kept for the day one of them widens.
            prologue.Add(Ast.IfThen(
                Ast.NotEqual(
                    Ast.Call(IronRuby.Compiler.Methods.GetActiveRefinements,
                        Ast.Convert(Ast.ArrayIndex(argsParameter, Ast.Constant(scopeArgIndex)), typeof(RubyScope))),
                    Ast.Constant(refinements, typeof(RefinementActivation))
                ),
                Ast.Return(exit, Ast.Constant(OsrLoopSite.RefinementsChanged, typeof(object)))
            ));

            if (self != null) {
                prologue.Add(Ast.Assign(self, Ast.ArrayIndex(argsParameter, Ast.Constant(selfArgIndex))));
            }

            // Nothing may have redefined Integer#+ (or anything else) since the types were
            // read: one global version covers every def, undef, alias and include. A copy that
            // fails it fails it on every later entry, so it asks to be rebuilt under the new
            // version rather than counting as one more deopt towards retiring the loop.
            prologue.Add(Ast.IfThen(
                Ast.Not(Ast.Call(JitRuntime.M("VersionOk"), Ast.Constant(version))),
                Ast.Return(exit, Ast.Call(JitRuntime.M("OsrVersionStale")))
            ));
            // and no constant may have changed since the ones the body folded were read
            if (facts.UsesConstants) {
                prologue.Add(Ast.IfThen(
                    Ast.Not(Ast.Call(JitRuntime.M("ConstantsOk"), Ast.Constant(context), Ast.Constant(constantVersion))),
                    Ast.Return(exit, Ast.Call(JitRuntime.M("OsrVersionStale")))
                ));
            }
            // The Arrays the body indexes directly, each loaded once: a local that does not hold a plain
            // Array this time hands the loop back, for a copy built over what it holds now.
            if (facts.Arrays != null) {
                foreach (var array in facts.Arrays) {
                    locals.Add(array.Key);
                    prologue.Add(Ast.Assign(array.Key, array.Value));
                    prologue.Add(Ast.IfThen(
                        Ast.Equal(array.Key, Ast.Constant(null, typeof(RubyArray))),
                        Ast.Return(exit, Ast.Call(JitRuntime.M("OsrTypeGuardFailed"), Ast.Constant(array.Key.Name)))
                    ));
                }
            }
            if (facts.UndoLog != null) {
                // the element writes the current iteration makes, for a deopt to take back
                prologue.Add(Ast.Assign(facts.UndoLog, Ast.Property(null, typeof(JitUndoLog).GetProperty("Current"))));
                prologue.Add(Ast.Assign(facts.UndoMark, Ast.Property(facts.UndoLog, typeof(JitUndoLog).GetProperty("Mark"))));
            }

            var storeCurrent = new List<MSA.Expression>();
            var storeSnapshot = new List<MSA.Expression>();
            foreach (var slot in slots) {
                locals.Add(slot.Variable);
                locals.Add(slot.Snapshot);
                Type clr = slot.Variable.Type;

                // A long goes home through the representation funnel - a value that fits in an
                // Int32 must arrive back in the tuple as one - and comes in from either an Int32
                // or an Int64, since both are the same Ruby Integer and only the magnitude says
                // which one a given value is.
                bool isLong = (slot.Type == JT.Lng);
                bool isObj = (slot.Type == JT.Obj);
                // an object slot holds whatever the tuple holds
                MSA.Expression holds = isLong
                    ? Ast.Call(JitRuntime.M("IsIntegral"), probe)
                    : isObj ? Ast.Constant(true) : (MSA.Expression)Ast.TypeIs(probe, clr);
                MSA.Expression read = isLong
                    ? Ast.Call(JitRuntime.M("ToLong"), probe)
                    : isObj ? probe : (MSA.Expression)Ast.Unbox(probe, clr);

                MSA.Expression store = Ast.Assign(slot.Accessor, Box(slot.Variable, isLong));
                MSA.Expression storeSnap = Ast.Assign(slot.Accessor, Box(slot.Snapshot, isLong));
                if (slot.Assigned != null) {
                    locals.Add(slot.Assigned);
                    locals.Add(slot.AssignedSnapshot);
                    // Take the entry value when it is the type the loop wants - usually it is,
                    // because a previous run left it there - and otherwise start the slot out
                    // as unwritten rather than refuse the whole loop.
                    prologue.Add(Ast.Assign(probe, slot.Accessor));
                    prologue.Add(Ast.Assign(slot.Assigned, holds));
                    prologue.Add(Ast.IfThen(slot.Assigned, Ast.Assign(slot.Variable, read)));
                    prologue.Add(Ast.Assign(slot.Snapshot, slot.Variable));
                    prologue.Add(Ast.Assign(slot.AssignedSnapshot, slot.Assigned));
                    store = Ast.IfThen(slot.Assigned, store);
                    storeSnap = Ast.IfThen(slot.AssignedSnapshot, storeSnap);
                } else {
                    prologue.Add(Ast.Assign(probe, slot.Accessor));
                    if (!isObj) {
                        prologue.Add(Ast.IfThen(
                            Ast.Not(holds),
                            Ast.Return(exit, Ast.Call(JitRuntime.M("OsrTypeGuardFailed"), Ast.Constant(slot.Variable.Name)))
                        ));
                    }
                    prologue.Add(Ast.Assign(slot.Variable, read));
                    prologue.Add(Ast.Assign(slot.Snapshot, slot.Variable));
                }
                storeCurrent.Add(store);
                storeSnapshot.Add(storeSnap);
            }

            // The loop runs, the locals go home, and the value is nil: a `while' has no other
            // value here, since a `break' with an argument makes the emitter give up.
            MSA.Expression run = Ast.Block(typeof(object),
                body, Ast.Block(storeCurrent.ToArray()), Ast.Constant(null, typeof(object))
            );

            if (canDeopt) {
                if (facts.UndoLog != null) {
                    // the generic loop re-runs the iteration: first take back what it wrote
                    storeSnapshot.Insert(0, Ast.Call(facts.UndoLog, typeof(JitUndoLog).GetMethod("Rollback"), facts.UndoMark));
                }
                storeSnapshot.Add(Ast.Constant(OsrLoopSite.Retry, typeof(object)));
                run = Ast.TryCatch(run,
                    Ast.Catch(typeof(JitDeoptException), Ast.Block(typeof(object), storeSnapshot.ToArray())));
            }
            if (facts.UndoLog != null) {
                // the writes of a run that ended stand, however it ended
                run = Ast.TryFinally(run, Ast.Call(facts.UndoLog, typeof(JitUndoLog).GetMethod("Release"), facts.UndoMark));
            }

            prologue.Add(run);

            var lambda = Ast.Lambda<Func<object[], object>>(
                Ast.Label(exit, Ast.Block(typeof(object), locals, prologue)),
                "osrjit$" + loop.Location.Start.Line,
                new[] { argsParameter }
            );

            try {
                return lambda.Compile();
            } catch (Exception) {
                return null;
            }
        }
    }

    /// <summary>Whether an expression tree references a given parameter or variable.</summary>
    internal sealed class UsesParameter : MSA.ExpressionVisitor {
        private readonly MSA.ParameterExpression/*!*/ _parameter;
        private bool _found;

        private UsesParameter(MSA.ParameterExpression/*!*/ parameter) {
            _parameter = parameter;
        }

        internal static bool In(MSA.Expression/*!*/ tree, MSA.ParameterExpression/*!*/ parameter) {
            var finder = new UsesParameter(parameter);
            finder.Visit(tree);
            return finder._found;
        }

        public override MSA.Expression Visit(MSA.Expression node) {
            return _found ? node : base.Visit(node);
        }

        protected override MSA.Expression VisitParameter(MSA.ParameterExpression/*!*/ node) {
            if (node == _parameter) {
                _found = true;
            }
            return node;
        }
    }
}

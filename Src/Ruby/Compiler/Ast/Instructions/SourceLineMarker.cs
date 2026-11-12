/* ****************************************************************************
 *
 * The line a compiled frame is at, for its backtrace.
 *
 * The interpreter keeps the line of each statement from its sequence point (a DebugInfoExpression), and a
 * backtrace reads it from the interpreted frame. System.Linq.Expressions' compiler, which the DLR uses for
 * a lambda that has run often enough (adaptive compilation), drops sequence points: a DynamicMethod has
 * no PDB, and the runtime keeps no IL offsets for its frames either (StackFrame.GetILOffset is -1). A
 * compiled frame was reported at the line its method or block starts on - the only line its name carries:
 * after ~300 calls, `raise' in line 2 of a method came out as raised in line 1, and so did every caller.
 *
 * So a compiled method or block keeps its line where the backtrace can read it: in a frame of
 * PrecompiledFrames, the thread's stack of compiled frames that the startup library already keeps for
 * Thread#backtrace. CompiledFrameExpression wraps the body of each method, block and file; the
 * interpreter compiles it to the bare body (an interpreted frame has its line already), and reduced for
 * the expression compiler it enters a frame, sets its line at each statement (each SourceLineMarker that
 * AstGenerator.AddDebugInfo puts after a sequence point) and leaves it, in a fault block when an exception
 * leaves it. RubyStackTraceBuilder matches the compiled frames of a CLR stack trace with these, by name.
 *
 * It costs a few ns per call of a compiled method or block (Release: up to ~10% on call microbenchmarks), as
 * long as finding the thread's frames is not a thread-static read in the compiled code: in a DynamicMethod
 * that is a call into the runtime, which made block and yield microbenchmarks 25-36% slower (#168). See
 * PrecompiledFrames.Current.
 *
 * ***************************************************************************/

using MSA = System.Linq.Expressions;

using System;
using IronRuby.Runtime;
using IronRuby.Runtime.Precompiled;
using Microsoft.Scripting.Interpreter;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// A statement's line. Nothing to the interpreter; in a compiled <see cref="CompiledFrameExpression"/>, the frame's
    /// line is set to it. Reduced, it is the line of a loop the interpreter has compiled (the only code that reduces it: every
    /// compiled Ruby lambda is a CompiledFrameExpression, and saved code drops it), kept for the interpreted frame the loop
    /// runs in: in the frame's line record, which a statement of a loop has in a variable of the frame (a null check and a
    /// store per statement, the record looked up once per run of the loop), or through PrecompiledFrames.SetLoopLine.
    /// </summary>
    internal sealed class SourceLineMarker : ReducibleEmptyExpression, IInstructionProvider {
        private static readonly SourceLineMarker[]/*!*/ _cache = new SourceLineMarker[4096];

        private readonly int _line;
        private readonly MSA.Expression _loopLineVariable;
        private readonly bool _inEnsure;

        private SourceLineMarker(int line, MSA.Expression loopLineVariable, bool inEnsure) {
            _line = line;
            _loopLineVariable = loopLineVariable;
            _inEnsure = inEnsure;
        }

        internal static SourceLineMarker/*!*/ Create(int line) {
            return Create(line, null, false);
        }

        /// <param name="loopLineVariable">The frame's line record variable of the loop the statement is in, if any.</param>
        /// <param name="inEnsure">The statement is in an ensure clause of its frame (see <see cref="InEnsure"/>).</param>
        internal static SourceLineMarker/*!*/ Create(int line, MSA.Expression loopLineVariable, bool inEnsure) {
            if (loopLineVariable == null && !inEnsure && (uint)line < (uint)_cache.Length) {
                return _cache[line] ??= new SourceLineMarker(line, null, false);
            }
            return new SourceLineMarker(line, loopLineVariable, inEnsure);
        }

        /// <summary>
        /// The statement is in an ensure clause, which compiled code runs as a finally block - a funclet of the method - and the
        /// frame's record says so with the line negated (PrecompiledFrames.Frame.InEnsure). On .NET 9 and later an exception
        /// thrown there has the method twice in its own trace, the funclet and the method's body, which the backtrace leaves out.
        /// </summary>
        internal bool InEnsure {
            get { return _inEnsure; }
        }

        internal int Line {
            get { return _line; }
        }

        public void AddInstructions(LightCompiler/*!*/ compiler) {
            // the interpreter has the sequence point
        }

        public override MSA.Expression/*!*/ Reduce() {
            if (_loopLineVariable == null) {
                return Ast.Call(_SetLoopLine, Ast.Constant(_line));
            }
            // if (record == null) record = PrecompiledFrames.GetLoopLine(); record.Line = line;
            return Ast.Block(
                Ast.IfThen(Ast.Equal(_loopLineVariable, Ast.Constant(null, typeof(PrecompiledFrames.LoopLine))),
                    Ast.Assign(_loopLineVariable, Ast.Call(_GetLoopLine))
                ),
                Ast.Assign(Ast.Field(_loopLineVariable, _LineField), Ast.Constant(_line)),
                Ast.Empty()
            );
        }

        private static readonly System.Reflection.MethodInfo/*!*/ _SetLoopLine = typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.SetLoopLine));
        private static readonly System.Reflection.MethodInfo/*!*/ _GetLoopLine = typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.GetLoopLine), Type.EmptyTypes);
        private static readonly System.Reflection.FieldInfo/*!*/ _LineField = typeof(PrecompiledFrames.LoopLine).GetField(nameof(PrecompiledFrames.LoopLine.Line));
    }

    /// <summary>
    /// The body of a compiled method, block or file, in a frame of <see cref="PrecompiledFrames"/> (see above).
    /// </summary>
    internal sealed class CompiledFrameExpression : MSA.Expression, IInstructionProvider {
        private readonly MSA.Expression/*!*/ _body;
        private readonly string/*!*/ _encodedName;
        private readonly int _line;

        internal CompiledFrameExpression(MSA.Expression/*!*/ body, string/*!*/ encodedName, int line) {
            _body = body;
            _encodedName = encodedName;
            _line = line;
        }

        internal MSA.Expression/*!*/ Body {
            get { return _body; }
        }

        public override MSA.ExpressionType NodeType {
            get { return MSA.ExpressionType.Extension; }
        }

        public override Type/*!*/ Type {
            get { return _body.Type; }
        }

        public override bool CanReduce {
            get { return true; }
        }

        public void AddInstructions(LightCompiler/*!*/ compiler) {
            compiler.Compile(_body);
        }

        public override MSA.Expression/*!*/ Reduce() {
            var frames = Ast.Variable(typeof(PrecompiledFrames), "#frames");
            var depth = Ast.Variable(typeof(int), "#depth");
            var body = new LineSetter(frames, depth).Visit(_body);
            var result = (_body.Type != typeof(void)) ? Ast.Variable(_body.Type, "#result") : null;

            // An exception leaves the frame with Leave in a fault block, and the frame keeps its line for the exception's
            // backtrace, which is built where the exception is caught, after this; a return leaves it with Return.
            MSA.Expression run = Ast.TryFault(body, Ast.Call(frames, typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.Leave)), depth));
            return Ast.Block(_body.Type, (result != null) ? new[] { frames, depth, result } : new[] { frames, depth },
                Ast.Assign(frames, Ast.Property(null, typeof(PrecompiledFrames), nameof(PrecompiledFrames.Current))),
                Ast.Assign(depth, Ast.Call(frames, typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.Enter)),
                    Ast.Constant(_encodedName), Ast.Constant(_line))),
                (result != null) ? Ast.Assign(result, run) : run,
                Ast.Call(frames, typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.Return)), depth),
                (MSA.Expression)result ?? Ast.Empty()
            );
        }

        protected override MSA.Expression/*!*/ VisitChildren(MSA.ExpressionVisitor/*!*/ visitor) {
            var body = visitor.Visit(_body);
            return (body == _body) ? this : new CompiledFrameExpression(body, _encodedName, _line);
        }

        // the frame's line set at each statement of its own code - not of the lambdas nested in it, which have frames of their own
        private sealed class LineSetter : MSA.ExpressionVisitor {
            private static readonly System.Reflection.MethodInfo/*!*/ _SetLine = typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.SetLine));
            private readonly MSA.ParameterExpression/*!*/ _frames, _depth;

            internal LineSetter(MSA.ParameterExpression/*!*/ frames, MSA.ParameterExpression/*!*/ depth) {
                _frames = frames;
                _depth = depth;
            }

            protected override MSA.Expression/*!*/ VisitLambda<T>(MSA.Expression<T>/*!*/ node) {
                return node;
            }

            protected override MSA.Expression/*!*/ VisitExtension(MSA.Expression/*!*/ node) {
                var marker = node as SourceLineMarker;
                if (marker != null) {
                    return Ast.Call(_frames, _SetLine, _depth, Ast.Constant(marker.InEnsure ? -marker.Line : marker.Line));
                }
                if (node is CompiledFrameExpression) {
                    return node;
                }
                if (node is CompiledFrameDepthExpression) {
                    return _depth;
                }
                return base.VisitExtension(node);
            }
        }
    }

    /// <summary>
    /// The index of the record of the compiled frame the code is in (<see cref="CompiledFrameExpression"/>), which the frame's
    /// lambda has in a local: what tells the frame's record from the others' at run time (see RubyOps.CanRescueDeferTrace and
    /// RubyOps.PassEnsure). -1 where there is no such frame: interpreted, or in code saved ahead of time, which drops the
    /// CompiledFrameExpression. Only for code whose CLR method is the frame's lambda (AstGenerator.CurrentCompiledFrameDepth).
    /// </summary>
    internal sealed class CompiledFrameDepthExpression : MSA.Expression {
        public override MSA.ExpressionType NodeType {
            get { return MSA.ExpressionType.Extension; }
        }

        public override Type/*!*/ Type {
            get { return typeof(int); }
        }

        public override bool CanReduce {
            get { return true; }
        }

        public override MSA.Expression/*!*/ Reduce() {
            return Ast.Constant(-1);
        }

        protected override MSA.Expression/*!*/ VisitChildren(MSA.ExpressionVisitor/*!*/ visitor) {
            return this;
        }
    }
}

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
 * It costs about 2 ns per call of a compiled method or block (Release: 5-15% on call microbenchmarks).
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
    /// line is set to it.
    /// </summary>
    internal sealed class SourceLineMarker : ReducibleEmptyExpression, IInstructionProvider {
        private static readonly SourceLineMarker[]/*!*/ _cache = new SourceLineMarker[4096];

        private readonly int _line;

        private SourceLineMarker(int line) {
            _line = line;
        }

        internal static SourceLineMarker/*!*/ Create(int line) {
            if ((uint)line < (uint)_cache.Length) {
                return _cache[line] ??= new SourceLineMarker(line);
            }
            return new SourceLineMarker(line);
        }

        internal int Line {
            get { return _line; }
        }

        public void AddInstructions(LightCompiler/*!*/ compiler) {
            // the interpreter has the sequence point
        }
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
                    return Ast.Call(_frames, _SetLine, _depth, Ast.Constant(marker.Line));
                }
                if (node is CompiledFrameExpression) {
                    return node;
                }
                return base.VisitExtension(node);
            }
        }
    }
}

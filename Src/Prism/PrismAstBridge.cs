using System;
using System.Collections.Generic;
using System.Numerics;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Pm = IronRuby.Prism.Ast;

namespace IronRuby.Prism {
    /// <summary>
    /// Maps prism's AST (typed nodes decoded from the binary serialization by the
    /// generated PrismLoader) onto IronRuby.Compiler.Ast, so the existing
    /// AstGenerator/DLR pipeline compiles it unchanged. Modern syntax that the
    /// 1.9-era AST has no nodes for (safe navigation, keyword arguments, `it`)
    /// is lowered to equivalent constructs; unmapped nodes raise
    /// NotSupportedException naming the prism node type.
    /// </summary>
    public sealed class PrismAstBridge {
        private readonly string/*!*/ _source;
        private readonly string _path;
        private readonly int _lineOffset;
        // the offset each line starts at; the first _lineCount entries are used
        private readonly int[]/*!*/ _lineStarts;
        private readonly int _lineCount;
        // the line the last location was found on: nodes come in source order, so the next
        // lookup is almost always on that line or a few lines further down
        private int _lineHint;

        // The line the first line of this source is reported as. 1 for a file; whatever
        // eval/module_eval/Binding#eval was told for a string, which is what makes __LINE__
        // and backtraces inside the eval line up with the code it came from.
        private int _startLine = 1;
        private readonly RubyEncoding/*!*/ _encoding;
        private readonly Stack<LexicalScope>/*!*/ _scopes = new Stack<LexicalScope>();
        private int _tempCounter;
        private SourceUnit _sourceUnit;
        private ErrorSink _errorSink;
        // An eval whose code does not run inside a method: prism parses eval'd code as a partial
        // script and accepts a top-level yield, which MRI's compiler then rejects.
        private bool _evalOutsideMethod;
        // Whether the method being built uses its block (MethodDefinition.UsesBlock).
        private bool _usesBlock;
        // In a `case/in` or `=>`: the key a hash pattern last found missing, or nil. Decides
        // whether a failed match raises NoMatchingPatternKeyError rather than NoMatchingPatternError.
        private LocalVariable _patternMissingKey;
        // Where a failed match raises, the reason the last test failed - "String === 1 does not
        // return true", "[1, 2, 3] length mismatch (given 3, expected 2)" - so that the
        // NoMatchingPatternError says what MRI's says. Null where a failed match is just false.
        private LocalVariable _patternFailure;
        private bool _isEval;
        // case/in subject temp -> { value, "already computed" flag } holding its #deconstruct result
        private readonly Dictionary<LocalVariable, LocalVariable[]>/*!*/ _deconstructCache =
            new Dictionary<LocalVariable, LocalVariable[]>();

        // The Ruby-written core: its @__name__ instance variables are its own hidden state (RubyUtils.GetCoreStateInstanceVariableName).
        private readonly bool _hidesCoreState;

        private string/*!*/ IvarName(string/*!*/ name) {
            return _hidesCoreState && RubyUtils.IsCoreStateInstanceVariableName(name) ? RubyUtils.GetCoreStateInstanceVariableName(name) : name;
        }

        private PrismAstBridge(string/*!*/ source, string path, RubyEncoding/*!*/ encoding) {
            _source = source;
            // an eval with a starting line below 1 carries the difference in its file name
            _lineOffset = RubyUtils.DecodeEvalLineOffset(ref path);
            _path = path;
            _hidesCoreState = RubyUtils.KeepsHiddenState(path);
            _encoding = encoding;
            var lineStarts = new List<int> { 0 };
            for (int i = source.IndexOf('\n'); i >= 0; i = source.IndexOf('\n', i + 1)) {
                lineStarts.Add(i + 1);
            }
            _lineStarts = lineStarts.ToArray();
            _lineCount = _lineStarts.Length;
        }

        // IR_EAGER_DEF_BODIES=1 maps every method body at parse time, as before lazy mapping (for measuring it)
        private static readonly bool _EagerDefBodies = Environment.GetEnvironmentVariable("IR_EAGER_DEF_BODIES") == "1";

        // eval in an isolated proc: the outer locals it may not use (RubyCompilerOptions.IsolatedLocalNames)
        private ICollection<string> _isolatedLocalNames;

        public static SourceUnitTree Parse(SourceUnit/*!*/ sourceUnit, RubyCompilerOptions/*!*/ options, ErrorSink/*!*/ errorSink) {
            // eval("...", binding, file, line) reports __LINE__ and backtraces from `line`, which is
            // what RubyCompilerOptions.InitialLocation carries. Only the old parser ever read it, so
            // every eval under prism started at line 1 no matter what it was given.
            byte commandLine = 0;
            bool mainScript = false;
            if (options.FactoryKind == TopScopeFactoryKind.Main && sourceUnit.LanguageContext is RubyContext context) {
                commandLine = CommandLineFlags(context.RubyOptions, sourceUnit.Path);
                mainScript = sourceUnit.Path != "-e";
            }
            if (IronRuby.Runtime.Precompiled.PrecompiledCode.Trace) {
                Console.Error.WriteLine("IronRuby.Startup: parsing " + (sourceUnit.Path ?? "(no path)") + " (" + sourceUnit.GetCode().Length + " chars, " + options.FactoryKind + ")");
            }
            bool timed = PhaseTimer.Enter(PhaseTimer.Bridge);
            try {
                string code = sourceUnit.GetCode();
                // Prism, told this is the main script, would skip a #! line without "ruby" in it to
                // look for one that has; a binstub that names IronRuby's launcher is already Ruby.
                if (mainScript && (commandLine & 0x20) == 0 && RubyUtils.ShebangNamesIronRuby(code)) {
                    mainScript = false;
                }
                return ParseText(code, sourceUnit.Path, options.LocalNames, sourceUnit, errorSink,
                    options.InitialLocation.Line, options.IsEval && options.TopLevelMethodName == null, options.IsEval,
                    options.EvalSourceEncoding, commandLine, mainScript, options.IsolatedLocalNames);
            } finally {
                PhaseTimer.Exit(timed);
            }
        }

        /// <summary>
        /// The main program is parsed the way MRI parses it: prism itself wraps the -n / -p loop
        /// around it (with -a's split and -l's chomp), so BEGIN and END are still top-level
        /// statements, and -x has prism skip to the first #!...ruby line.
        /// </summary>
        private static byte CommandLineFlags(RubyOptions/*!*/ options, string path) {
            byte flags = 0;
            if (options.AutoSplit) flags |= 0x1;           // PM_OPTIONS_COMMAND_LINE_A
            if (path == "-e") flags |= 0x2;               // PM_OPTIONS_COMMAND_LINE_E: MRI keeps some warnings out of -e code
            if (options.ChopLines) flags |= 0x4;           // PM_OPTIONS_COMMAND_LINE_L
            if (options.LoopOverInput) flags |= 0x8;       // PM_OPTIONS_COMMAND_LINE_N
            if (options.PrintEachLine) flags |= 0x10;      // PM_OPTIONS_COMMAND_LINE_P
            if (options.SkipToRubyShebang && path != "-e") {
                flags |= 0x20;                             // PM_OPTIONS_COMMAND_LINE_X
            }
            return flags;
        }

        public static SourceUnitTree ParseText(string/*!*/ code, string path) {
            return ParseText(code, path, null, null, null);
        }

        public static SourceUnitTree ParseText(string/*!*/ code, string path, List<string> outerLocalNames,
            SourceUnit sourceUnit, ErrorSink errorSink) {
            return ParseText(code, path, outerLocalNames, sourceUnit, errorSink, 1);
        }

        public static SourceUnitTree ParseText(string/*!*/ code, string path, List<string> outerLocalNames,
            SourceUnit sourceUnit, ErrorSink errorSink, int startLine, bool evalOutsideMethod = false, bool isEval = false,
            RubyEncoding evalSourceEncoding = null, byte commandLine = 0, bool mainScript = false,
            ICollection<string> isolatedLocalNames = null) {

            // --enable/--disable=frozen-string-literal only sets the default; the magic comment
            // in a file still wins, and prism applies that rule itself.
            var context = sourceUnit != null ? sourceUnit.LanguageContext as RubyContext : null;
            int frozenStringLiteral = context != null ? context.RubyOptions.FrozenStringLiteral : 0;

            // The magic comment has to be read before prism is called rather than taken from
            // what it reports, because it decides which bytes prism is given in the first place.
            string declaredEncodingName = DeclaredEncodingName(code);
            RubyEncoding sourceEncoding = ResolveEncoding(declaredEncodingName, sourceUnit);

            // An eval'd string in a multibyte encoding other than UTF-8 is parsed as the bytes it
            // holds, and prism has to be told which encoding they are in.
            string encodingName = null;
            if (evalSourceEncoding != null && declaredEncodingName == null) {
                // A binary MutableString still arrives here as the Unicode characters decoded
                // from its bytes. Re-encode those as UTF-8 to recover the original byte sequence,
                // but tell prism to interpret that sequence as ASCII-8BIT. Using BinaryEncoding
                // itself would reject every character above U+00FF before prism sees it.
                if (evalSourceEncoding == RubyEncoding.Binary) {
                    sourceEncoding = RubyEncoding.UTF8;
                    encodingName = evalSourceEncoding.Name;
                } else {
                    sourceEncoding = evalSourceEncoding;
                    if (evalSourceEncoding != RubyEncoding.UTF8) {
                        encodingName = evalSourceEncoding.Name;
                    }
                }
            }

            // -K names the encoding of the main program - the script or -e, not what it requires -
            // unless a magic comment says otherwise.
            RubyEncoding kcode = context != null ? context.RubyOptions.DefaultEncoding : null;
            if (kcode != null && kcode != RubyEncoding.UTF8 && !isEval && declaredEncodingName == null &&
                sourceUnit.Path != null && sourceUnit.Path == context.RubyOptions.MainFile) {
                sourceEncoding = kcode;
                encodingName = kcode.Name;
            } else if (kcode == null && context != null && !isEval && declaredEncodingName == null &&
                (sourceUnit.Path == "-e" || sourceUnit.Path == "-") && sourceUnit.Path == context.RubyOptions.MainFile) {
                // Without -K, -e and a program read from stdin are in the locale's encoding (a file
                // is UTF-8): under LANG=C their literals and __ENCODING__ are US-ASCII. Their bytes
                // reach here as UTF-8 (with escapes for what is not), so only prism is told.
                RubyEncoding locale = context.RubyOptions.LocaleEncoding;
                if (locale != RubyEncoding.UTF8 && locale.IsAsciiIdentity) {
                    encodingName = locale.Name;
                }
            }

            // Method bodies are mapped when the method is first called (see Def) - most methods a file
            // defines never are. Not for eval'd code, seldom around long enough to pay that back, nor while
            // Coverage measures: its walk of the tree wants every body.
            bool deferBodies = !isEval && context != null && context.Coverage == null && !_EagerDefBodies;

            // A UTF-8 source may hold bytes that are not UTF-8, read in as escapes (see
            // RubyContext.GetSourceReader); the escaping encoding gives prism those bytes back.
            PrismParseResult result = PrismParser.Parse(code, path, startLine <= 0 ? 1 : startLine, outerLocalNames,
                frozenStringLiteral, sourceEncoding == RubyEncoding.UTF8 ? sourceEncoding.EscapingEncoding : sourceEncoding.Encoding,
                encodingName, commandLine, mainScript, evalSourceEncoding != null && declaredEncodingName == null, deferBodies);

            // An eval'd string without a magic comment is in the string's own encoding, which its
            // literals and __ENCODING__ then carry (MRI), rather than in UTF-8 as a file would be.
            RubyEncoding literalEncoding = ResolveEncoding(result.EncodingName, sourceUnit);
            if (evalSourceEncoding != null && declaredEncodingName == null) {
                literalEncoding = evalSourceEncoding;
            }

            var bridge = new PrismAstBridge(code, path, literalEncoding);
            bridge._startLine = startLine <= 0 ? 1 : startLine;
            bridge._sourceUnit = sourceUnit;
            bridge._errorSink = errorSink;
            bridge._evalOutsideMethod = evalOutsideMethod;
            bridge._isEval = isEval;
            bridge._isolatedLocalNames = isolatedLocalNames;

            if (result.Errors.Count > 0) {
                if (errorSink != null && sourceUnit != null) {
                    // A SyntaxError's message is MRI's: "syntax errors found" and prism's picture of where,
                    // at the line of the first error prism found (not the first in the source). Any other
                    // sink - a host's - is told of each error.
                    string formatted = (errorSink is RuntimeErrorSink) ? SyntaxErrorDescription(result, bridge._startLine + bridge._lineOffset) : null;
                    if (formatted != null) {
                        errorSink.Add(sourceUnit, formatted, bridge.Span(result.Errors[0].Location), 0, Severity.FatalError);
                        return null;
                    }
                    foreach (var error in result.Errors) {
                        errorSink.Add(sourceUnit, error.Message, bridge.Span(error.Location), 0, Severity.FatalError);
                    }
                    return null;
                }
                var first = result.Errors[0];
                throw new NotSupportedException(
                    $"syntax error: {first.Message} (line {bridge.Location(first.Location.Start).Line})");
            }

            if (errorSink != null && sourceUnit != null) {
                foreach (var warning in result.Warnings) {
                    // prism levels: 0 = always, 1 = only when $VERBOSE is true
                    errorSink.Add(sourceUnit, warning.Message, bridge.Span(warning.Location),
                        warning.Level == 0 ? Errors.RuntimeWarning : Errors.RuntimeVerboseWarning, Severity.Warning);
                }
            }

            return bridge.Program((Pm.ProgramNode)result.Root, outerLocalNames, result.DataOffset);
        }

        /// <summary>
        /// What a SyntaxError says after its "file:line: ": prism's layout of the errors (see
        /// PrismParser.FormatErrors) without the location prism puts in front of it, which the
        /// error sink adds - eval decodes the file name and line its own way. Colored, as MRI's is,
        /// when $stderr is a terminal (bold only under NO_COLOR).
        /// </summary>
        private static string SyntaxErrorDescription(PrismParseResult/*!*/ result, int startLine) {
            int formatType = 1;
            if (!Console.IsErrorRedirected) {
                formatType = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) ? 3 : 2;
            }
            string formatted;
            try {
                formatted = PrismParser.FormatErrors(result, formatType, startLine);
            } catch (Exception) {
                // no layout: the first error's message, as before
                return null;
            }
            if (formatted == null) {
                return null;
            }
            string header = (result.Errors.Count > 1) ? ": syntax errors found\n" : ": syntax error found\n";
            int end = formatted.IndexOf('\n');
            int at = (end < 0) ? -1 : formatted.LastIndexOf(header, end, StringComparison.Ordinal);
            return (at < 0) ? null : formatted.Substring(at + 2);
        }

        /// <summary>
        /// prism reports the encoding it settled on for the source — from a magic
        /// comment, a BOM, or the UTF-8 default — and every literal has to carry it.
        /// Tagging everything UTF-8 makes a `# encoding: binary` file produce
        /// UTF-8 strings that compare unequal to the ASCII-8BIT ones the runtime
        /// returns, which is what made almost all of spec/library/digest fail: the
        /// bytes matched, the encodings did not.
        /// </summary>
        /// <summary>
        /// The encoding a `# coding:` comment asks for, which sits on the first line or on the
        /// second when a shebang takes the first. Null when there is none - the usual case, and
        /// the one that means UTF-8.
        /// </summary>
        private static string DeclaredEncodingName(string/*!*/ code) {
            int start = 0;
            for (int line = 0; line < 2 && start < code.Length; line++) {
                int end = code.IndexOf('\n', start);
                if (end < 0) {
                    end = code.Length;
                }

                string text = code.Substring(start, end - start).TrimStart(' ', '\t', '\f');
                if (!text.StartsWith("#", StringComparison.Ordinal)) {
                    return null;
                }

                // "# CoDiNg: bIg5" too; no Regex, building one (RegexOptions.Compiled) was 8 ms of startup
                string name = Tokenizer.MatchCodingComment(text);
                if (name != null) {
                    return name;
                }

                // Only a shebang lets the comment slip to the second line.
                if (line == 0 && !text.StartsWith("#!", StringComparison.Ordinal)) {
                    return null;
                }
                start = end + 1;
            }
            return null;
        }

        private static RubyEncoding/*!*/ ResolveEncoding(string name, SourceUnit sourceUnit) {
            if (String.IsNullOrEmpty(name)) {
                return RubyEncoding.UTF8;
            }

            switch (name.ToUpperInvariant()) {
                case "UTF-8": return RubyEncoding.UTF8;
                case "BINARY":
                case "ASCII-8BIT": return RubyEncoding.Binary;
                case "US-ASCII":
                case "ASCII": return RubyEncoding.Ascii;
            }

            // anything else needs the context's name table (aliases, code pages)
            var context = (sourceUnit != null) ? sourceUnit.LanguageContext as RubyContext : null;
            if (context != null) {
                try {
                    return context.GetRubyEncoding(name);
                } catch (Exception) {
                    // prism accepted the name, so keep the tree rather than failing
                    // the parse over an encoding we cannot map onto a .NET code page
                }
            }
            return RubyEncoding.UTF8;
        }

        // ---- helpers ----

        private SourceSpan Span(Pm.PmNode node) {
            return Span(node.StartOffset, node.Length);
        }

        private SourceSpan Span(Pm.PmLocation location) {
            return Span(location.Start, location.Length);
        }

        private SourceSpan Span(int start, int length) {
            int startLine = FindLine(start);
            // the end is at or after the start: search on from the start's line
            int end = start + length;
            int endLine = FindLineFrom(end, startLine);
            return new SourceSpan(MakeLocation(start, startLine), MakeLocation(end, endLine));
        }

        private string/*!*/ IdentifierText(string/*!*/ name) {
            if (_encoding != RubyEncoding.Binary) {
                return name;
            }
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) {
                chars[i] = (char)bytes[i];
            }
            return new string(chars);
        }

        private SourceLocation Location(int index) {
            return MakeLocation(index, FindLine(index));
        }

        private SourceLocation MakeLocation(int index, int line) {
            if (index > _source.Length) index = _source.Length;
            return new SourceLocation(index, line + _startLine, index - _lineStarts[line] + 1);
        }

        /// <summary>
        /// The last line that starts at or before <paramref name="index"/> (what a binary search of
        /// the line starts finds), looked for from the line the previous lookup ended on.
        /// </summary>
        private int FindLine(int index) {
            int hint = _lineHint;
            int line = (_lineStarts[hint] <= index) ? FindLineFrom(index, hint) : FindLineFrom(index, 0);
            _lineHint = line;
            return line;
        }

        // the same, given a line that starts at or before index
        private int FindLineFrom(int index, int line) {
            int[] starts = _lineStarts;
            int count = _lineCount;
            for (int i = 0; i < 4; i++) {
                if (line + 1 >= count || starts[line + 1] > index) {
                    return line;
                }
                line++;
            }
            int lo = line, hi = count - 1;
            while (lo < hi) {
                int mid = lo + ((hi - lo + 1) >> 1);
                if (starts[mid] <= index) {
                    lo = mid;
                } else {
                    hi = mid - 1;
                }
            }
            return lo;
        }

        private Exception Unsupported(Pm.PmNode node) {
            var span = Span(node);
            return new NotSupportedException(
                $"Prism bridge: node type '{node.GetType().Name}' is not supported yet (line {span.Start.Line})");
        }

        private LexicalScope/*!*/ CurrentScope {
            get { return _scopes.Peek(); }
        }

        private static bool HasFlag(Pm.PmNode node, uint flag) {
            return (node.Flags & flag) != 0;
        }

        // regex literals become $_ matches, ranges become flip-flops, etc.
        private Expression/*!*/ Condition(Pm.PmNode/*!*/ predicate) {
            return UnfrozenDefined(Expr(predicate)).ToCondition(CurrentScope);
        }

        /// <summary>
        /// `if defined?(x)', `while defined?(x) && ...': in a condition only whether the answer is nil counts, so the string
        /// need not be frozen by a call to #freeze (see Freeze) - which CRuby's defined? does not make at all, and which, being a
        /// call, made the method it is in need its scope (`return ... unless defined?(yield)', MethodDefinition.IsBlockGuard).
        /// The operands of &amp;&amp;, || and ! in a condition are conditions too: what one of them gives back is only tested.
        /// </summary>
        private static Expression/*!*/ UnfrozenDefined(Expression/*!*/ condition) {
            switch (condition) {
                case MethodCall call when call.MethodName == "freeze" && call.Target is IsDefinedExpression &&
                    call.Arguments == null && call.Block == null:
                    return call.Target;

                case AndExpression and: {
                    var left = UnfrozenDefined(and.Left);
                    var right = UnfrozenDefined(and.Right);
                    return (left == and.Left && right == and.Right) ? condition : new AndExpression(left, right, and.Location);
                }
                case OrExpression or: {
                    var left = UnfrozenDefined(or.Left);
                    var right = UnfrozenDefined(or.Right);
                    return (left == or.Left && right == or.Right) ? condition : new OrExpression(left, right, or.Location);
                }
                case NotExpression not: {
                    var operand = UnfrozenDefined(not.Expression);
                    return (operand == not.Expression) ? condition : new NotExpression(operand, not.Location);
                }
                default:
                    return condition;
            }
        }

        // ---- program / statements ----

        private SourceUnitTree/*!*/ Program(Pm.ProgramNode/*!*/ node, List<string> outerLocalNames, int dataOffset) {
            var scope = new TopStaticLexicalScope(
                outerLocalNames != null ? new RuntimeLexicalScope(outerLocalNames, _isolatedLocalNames) : null);
            _scopes.Push(scope);
            var statements = BuildStatements(HoistPreExecution(node.Statements));
            _scopes.Pop();
            return new SourceUnitTree(scope, statements, _encoding, dataOffset);
        }

        /// <summary>
        /// BEGIN blocks run before the rest of the code unit, in the order they appear, however
        /// far down they are written. They can only be top-level statements.
        /// </summary>
        private static Pm.PmNode HoistPreExecution(Pm.PmNode statementsNode) {
            var statements = statementsNode as Pm.StatementsNode;

            // -n / -p: prism has put the program inside `while gets`, BEGIN blocks and all. They
            // still run once, before the loop.
            if (statements != null && statements.Body.Length == 1 && statements.Body[0] is Pm.WhileNode loop &&
                loop.Statements is Pm.StatementsNode loopBody && Array.Exists(loopBody.Body, n => n is Pm.PreExecutionNode)) {
                var hoisted = new List<Pm.PmNode>();
                var rest = new List<Pm.PmNode>();
                foreach (var n in loopBody.Body) {
                    if (n is Pm.PreExecutionNode preExec) {
                        CollectPreExecution(preExec, hoisted);
                    } else {
                        rest.Add(n);
                    }
                }
                loop.Statements = new Pm.StatementsNode {
                    Body = rest.ToArray(), StartOffset = loopBody.StartOffset, Length = loopBody.Length, Flags = loopBody.Flags
                };
                hoisted.Add(loop);
                return new Pm.StatementsNode {
                    Body = hoisted.ToArray(), StartOffset = statements.StartOffset, Length = statements.Length, Flags = statements.Flags
                };
            }

            if (statements == null || !Array.Exists(statements.Body, n => n is Pm.PreExecutionNode)) {
                return statementsNode;
            }
            var reordered = new List<Pm.PmNode>();
            foreach (var n in statements.Body) {
                if (n is Pm.PreExecutionNode preExec) CollectPreExecution(preExec, reordered);
            }
            foreach (var n in statements.Body) {
                if (!(n is Pm.PreExecutionNode)) reordered.Add(n);
            }
            return new Pm.StatementsNode {
                Body = reordered.ToArray(), StartOffset = statements.StartOffset, Length = statements.Length, Flags = statements.Flags
            };
        }

        /// <summary>
        /// Adds a BEGIN block to <paramref name="into"/>, after the BEGIN blocks written inside it:
        /// MRI's parser registers a BEGIN when it is closed, so `BEGIN { a; BEGIN { b } }` runs b
        /// first, and then a without the inner block.
        /// </summary>
        private static void CollectPreExecution(Pm.PreExecutionNode/*!*/ node, List<Pm.PmNode>/*!*/ into) {
            // Nested BEGINs recurse through here rather than through Expr (see StackGuard).
            if (!StackGuard.IsSufficient) {
                StackGuard.RunOnFreshStack(() => CollectPreExecution(node, into));
                return;
            }
            if (node.Statements is Pm.StatementsNode body && Array.Exists(body.Body, n => n is Pm.PreExecutionNode)) {
                var rest = new List<Pm.PmNode>();
                foreach (var n in body.Body) {
                    if (n is Pm.PreExecutionNode inner) {
                        CollectPreExecution(inner, into);
                    } else {
                        rest.Add(n);
                    }
                }
                node.Statements = new Pm.StatementsNode {
                    Body = rest.ToArray(), StartOffset = body.StartOffset, Length = body.Length, Flags = body.Flags
                };
            }
            into.Add(node);
        }

        private Statements/*!*/ BuildStatements(Pm.PmNode statementsNode) {
            var result = new Statements();
            if (statementsNode is Pm.StatementsNode statements) {
                var body = statements.Body;
                for (int i = 0; i < body.Length; i++) {
                    result.Add((i < body.Length - 1) ? VoidStatement(body[i]) : Expr(body[i]), StatementLine(body[i]));
                }
            } else if (statementsNode != null) {
                result.Add(Expr(statementsNode));
            }
            return result;
        }

        /// <summary>
        /// The line of a statement's TracePoint :line event. Prism gives a conditional or a loop's
        /// newline flag to its predicate, so `foo(...) if cond` spanning lines reports cond's line.
        /// </summary>
        private int StatementLine(Pm.PmNode/*!*/ node) {
            Pm.PmNode predicate = null;
            switch (node) {
                case Pm.IfNode ifNode: predicate = ifNode.Predicate; break;
                case Pm.UnlessNode unlessNode: predicate = unlessNode.Predicate; break;
                // (begin ... end while: MRI's events differ again; left at the statement's start)
                case Pm.WhileNode whileNode when !HasFlag(whileNode, Pm.LoopFlags.BeginModifier): predicate = whileNode.Predicate; break;
                case Pm.UntilNode untilNode when !HasFlag(untilNode, Pm.LoopFlags.BeginModifier): predicate = untilNode.Predicate; break;
            }
            return Span(predicate ?? node).Start.Line;
        }

        /// <summary>
        /// A statement whose value is discarded. CRuby compiles `defined?(...)` away entirely in
        /// that position -- the operand is never evaluated -- and warns about it, so do the same.
        /// </summary>
        private Expression/*!*/ VoidStatement(Pm.PmNode/*!*/ node) {
            var span = Span(node);
            if (!(Unparen(node) is Pm.DefinedNode defined)) {
                return Expr(node);
            }
            // still map the operand: it may introduce locals the rest of the scope refers to
            Expr(defined.Value);
            if (_errorSink != null && _sourceUnit != null) {
                _errorSink.Add(_sourceUnit, "possibly useless use of defined? in void context", span,
                    Errors.RuntimeVerboseWarning, Severity.Warning);
            }
            return Literal.Nil(span);
        }

        private Expression/*!*/ StatementsAsExpression(Pm.PmNode statementsNode, SourceSpan span) {
            var statements = BuildStatements(statementsNode);
            if (statements.Count == 1) {
                foreach (var s in statements) return s;
            }
            if (statements.Count == 0) {
                return Literal.Nil(span);
            }
            return new BlockExpression(statements, span);
        }

        // ---- expression dispatch ----

        private Expression/*!*/ Expr(Pm.PmNode/*!*/ node) {
            // Nesting recurses through here; prism accepts it deeper than a thread's stack goes.
            if (!StackGuard.IsSufficient) {
                return StackGuard.RunOnFreshStack(() => Expr(node));
            }
            var span = Span(node);
            switch (node) {
                case Pm.IntegerNode integer:
                    return BigIntegerLiteral(integer.Value, span);
                case Pm.FloatNode floatNode:
                    return Literal.Double(floatNode.Value, span);
                case Pm.RationalNode rational:
                    return new MethodCall(null, "Rational", new Arguments(new Expression[] {
                        BigIntegerLiteral(rational.Numerator, span), BigIntegerLiteral(rational.Denominator, span)
                    }), span);
                case Pm.ImaginaryNode imaginary:
                    return new MethodCall(null, "Complex", new Arguments(new Expression[] {
                        Literal.Integer(0, span), Expr(imaginary.Numeric)
                    }), span);
                case Pm.StringNode str:
                    // prism tracks the frozen_string_literal magic comment itself and flags each
                    // literal with the state in effect where it appears, so the comment's scope -
                    // whole file, and only the file it is in - comes out right without the bridge
                    // having to know anything about it.
                    return new StringLiteral(LiteralValue(str.Unescaped, LiteralEncoding(str)), LiteralEncoding(str),
                        StringMutability(str), span);
                case Pm.SymbolNode symbol:
                    return new SymbolLiteral(LiteralText(symbol.Unescaped, SymbolEncoding(symbol)), SymbolEncoding(symbol), span);
                case Pm.TrueNode _: return Literal.True(span);
                case Pm.FalseNode _: return Literal.False(span);
                case Pm.NilNode _: return Literal.Nil(span);
                case Pm.SelfNode _: return new SelfReference(span);

                case Pm.ParenthesesNode parens:
                    return StatementsAsExpression(parens.Body, span);
                case Pm.StatementsNode statements:
                    return StatementsAsExpression(statements, span);
                case Pm.ImplicitNode implicitNode:
                    return Expr(implicitNode.Value); // hash shorthand {x:} / omitted values
                case Pm.ShareableConstantNode shareable:
                    return Expr(shareable.Write);

                case Pm.ArrayNode array: {
                    var items = new List<Expression>();
                    foreach (var element in array.Elements) items.Add(Argument(element));
                    return new ArrayConstructor(new Arguments(items.ToArray()), span);
                }
                case Pm.HashNode hash:
                    return HashExpression(hash.Elements, false, span);
                case Pm.KeywordHashNode keywordHash:
                    // the trailing hash of a call written with keyword syntax: `f(a: 1)`
                    return HashExpression(keywordHash.Elements, true, span);
                case Pm.FlipFlopNode flipFlop: {
                    var rangeExpr = new RangeExpression(
                        flipFlop.Left != null ? Expr(flipFlop.Left) : Literal.Nil(span),
                        flipFlop.Right != null ? Expr(flipFlop.Right) : Literal.Nil(span),
                        HasFlag(flipFlop, Pm.RangeFlags.ExcludeEnd), span);
                    return rangeExpr.ToCondition(CurrentScope);
                }
                case Pm.RangeNode range:
                    return new RangeExpression(
                        range.Left != null ? Expr(range.Left) : Literal.Nil(span),
                        range.Right != null ? Expr(range.Right) : Literal.Nil(span),
                        HasFlag(range, Pm.RangeFlags.ExcludeEnd), span);

                case Pm.InterpolatedStringNode interp: {
                    // `"a" "b"' is one literal written in two pieces. prism reports it as an
                    // interpolated string with nothing interpolated; MRI treats it as the single
                    // literal it looks like, frozen or chilled with the rest of the file's.
                    var folded = TryFoldAdjacentLiterals(interp, span);
                    return folded ?? new StringConstructor(StringParts(interp.Parts), StringKind.Mutable, span);
                }
                case Pm.InterpolatedSymbolNode interpSymbol:
                    return new StringConstructor(StringParts(interpSymbol.Parts), StringKind.Symbol, span);
                case Pm.XStringNode xstr:
                    return new StringConstructor(
                        new List<Expression> { new StringLiteral(LiteralValue(xstr.Unescaped, LiteralEncoding(xstr)), LiteralEncoding(xstr), span) },
                        StringKind.Command, span);
                case Pm.InterpolatedXStringNode interpX:
                    return new StringConstructor(StringParts(interpX.Parts), StringKind.Command, span);
                case Pm.EmbeddedStatementsNode embedded:
                    return StatementsAsExpression(embedded.Statements, span);

                case Pm.RegularExpressionNode regex:
                    WarnRegexp(regex.Unescaped, span);
                    return new RegularExpression(
                        new List<Expression> { new StringLiteral(LiteralValue(regex.Unescaped, _encoding), _encoding, span) },
                        RegexOptions(regex), false, span);
                case Pm.MatchLastLineNode matchLast:
                    WarnRegexp(matchLast.Unescaped, span);
                    return new RegularExpression(
                        new List<Expression> { new StringLiteral(LiteralValue(matchLast.Unescaped, _encoding), _encoding, span) },
                        RegexOptions(matchLast), true, span);
                case Pm.InterpolatedRegularExpressionNode interpRegex:
                    return new RegularExpression(StringParts(interpRegex.Parts), RegexOptions(interpRegex), span);
                case Pm.InterpolatedMatchLastLineNode interpMatchLast:
                    return new RegularExpression(StringParts(interpMatchLast.Parts), RegexOptions(interpMatchLast), true, span);

                case Pm.CallNode call: return Call(call, span);

                case Pm.LocalVariableReadNode read:
                    return CurrentScope.ResolveOrAddVariable(read.Name, span);
                case Pm.ItLocalVariableReadNode _:
                    return CurrentScope.ResolveOrAddVariable("it", span);
                case Pm.LocalVariableWriteNode write:
                    return new SimpleAssignmentExpression(
                        CurrentScope.ResolveOrAddVariable(write.Name, span), Expr(write.Value), null, span);
                case Pm.LocalVariableOperatorWriteNode opWrite:
                    return new SimpleAssignmentExpression(
                        CurrentScope.ResolveOrAddVariable(opWrite.Name, span), Expr(opWrite.Value), opWrite.BinaryOperator, span);
                case Pm.LocalVariableOrWriteNode orWrite:
                    return new SimpleAssignmentExpression(
                        CurrentScope.ResolveOrAddVariable(orWrite.Name, span), Expr(orWrite.Value), "||", span);
                case Pm.LocalVariableAndWriteNode andWrite:
                    return new SimpleAssignmentExpression(
                        CurrentScope.ResolveOrAddVariable(andWrite.Name, span), Expr(andWrite.Value), "&&", span);

                case Pm.InstanceVariableReadNode ivarRead:
                    return new InstanceVariable(IvarName(ivarRead.Name), span);
                case Pm.InstanceVariableWriteNode ivarWrite:
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(IvarName(ivarWrite.Name), span), Expr(ivarWrite.Value), null, span);
                case Pm.InstanceVariableOperatorWriteNode ivarOp:
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(IvarName(ivarOp.Name), span), Expr(ivarOp.Value), ivarOp.BinaryOperator, span);
                case Pm.InstanceVariableOrWriteNode ivarOr:
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(IvarName(ivarOr.Name), span), Expr(ivarOr.Value), "||", span);
                case Pm.InstanceVariableAndWriteNode ivarAnd:
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(IvarName(ivarAnd.Name), span), Expr(ivarAnd.Value), "&&", span);

                case Pm.GlobalVariableReadNode gvarRead:
                    return new IronRuby.Compiler.Ast.GlobalVariable(gvarRead.Name.Substring(1), span);
                case Pm.GlobalVariableWriteNode gvarWrite:
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(gvarWrite.Name.Substring(1), span), Expr(gvarWrite.Value), null, span);
                case Pm.GlobalVariableOperatorWriteNode gvarOp:
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(gvarOp.Name.Substring(1), span), Expr(gvarOp.Value), gvarOp.BinaryOperator, span);
                case Pm.GlobalVariableOrWriteNode gvarOr:
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(gvarOr.Name.Substring(1), span), Expr(gvarOr.Value), "||", span);
                case Pm.GlobalVariableAndWriteNode gvarAnd:
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(gvarAnd.Name.Substring(1), span), Expr(gvarAnd.Value), "&&", span);

                case Pm.ClassVariableReadNode cvarRead:
                    return new ClassVariable(cvarRead.Name, span);
                case Pm.ClassVariableWriteNode cvarWrite:
                    return new SimpleAssignmentExpression(
                        new ClassVariable(cvarWrite.Name, span), Expr(cvarWrite.Value), null, span);
                case Pm.ClassVariableOperatorWriteNode cvarOp:
                    return new SimpleAssignmentExpression(
                        new ClassVariable(cvarOp.Name, span), Expr(cvarOp.Value), cvarOp.BinaryOperator, span);
                case Pm.ClassVariableOrWriteNode cvarOr:
                    return new SimpleAssignmentExpression(
                        new ClassVariable(cvarOr.Name, span), Expr(cvarOr.Value), "||", span);
                case Pm.ClassVariableAndWriteNode cvarAnd:
                    return new SimpleAssignmentExpression(
                        new ClassVariable(cvarAnd.Name, span), Expr(cvarAnd.Value), "&&", span);

                case Pm.ConstantReadNode constRead:
                    return new ConstantVariable(IdentifierText(constRead.Name), span);
                case Pm.ConstantPathNode constPath:
                    return ConstantPath(constPath, span);
                case Pm.ConstantWriteNode constWrite:
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(IdentifierText(constWrite.Name), span), Expr(constWrite.Value), null, span);
                case Pm.ConstantOperatorWriteNode constOp:
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(IdentifierText(constOp.Name), span), Expr(constOp.Value), constOp.BinaryOperator, span);
                case Pm.ConstantOrWriteNode constOr:
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(IdentifierText(constOr.Name), span), Expr(constOr.Value), "||", span);
                case Pm.ConstantAndWriteNode constAnd:
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(IdentifierText(constAnd.Name), span), Expr(constAnd.Value), "&&", span);
                case Pm.ConstantPathWriteNode constPathWrite:
                    return new SimpleAssignmentExpression(
                        ConstantPath(constPathWrite.Target, Span(constPathWrite.Target)), Expr(constPathWrite.Value), null, span);
                case Pm.ConstantPathOrWriteNode constPathOr:
                    return new SimpleAssignmentExpression(
                        ConstantPath(constPathOr.Target, Span(constPathOr.Target)), Expr(constPathOr.Value), "||", span);
                case Pm.ConstantPathAndWriteNode constPathAnd:
                    return new SimpleAssignmentExpression(
                        ConstantPath(constPathAnd.Target, Span(constPathAnd.Target)), Expr(constPathAnd.Value), "&&", span);
                case Pm.ConstantPathOperatorWriteNode constPathOp:
                    return new SimpleAssignmentExpression(
                        ConstantPath(constPathOp.Target, Span(constPathOp.Target)), Expr(constPathOp.Value), constPathOp.BinaryOperator, span);

                case Pm.IfNode ifNode: return If(ifNode, span);
                case Pm.UnlessNode unless: {
                    // Source order, for the reason If() gives.
                    var unlessCondition = Condition(unless.Predicate);
                    var unlessBody = BuildStatements(unless.Statements);
                    ElseIfClause elseClause = null;
                    if (unless.ElseClause is Pm.ElseNode elseNode) {
                        elseClause = new ElseIfClause(null, BuildStatements(elseNode.Statements), Span(elseNode));
                    }
                    return new UnlessExpression(unlessCondition, unlessBody, elseClause, span);
                }
                case Pm.WhileNode whileNode:
                    return new WhileLoopExpression(Condition(whileNode.Predicate), true,
                        HasFlag(whileNode, Pm.LoopFlags.BeginModifier), BuildStatements(whileNode.Statements), span);
                case Pm.UntilNode untilNode:
                    return new WhileLoopExpression(Condition(untilNode.Predicate), false,
                        HasFlag(untilNode, Pm.LoopFlags.BeginModifier), BuildStatements(untilNode.Statements), span);

                case Pm.AndNode and:
                    return new AndExpression(Expr(and.Left), Expr(and.Right), span);
                case Pm.OrNode or:
                    return new OrExpression(Expr(or.Left), Expr(or.Right), span);

                case Pm.CaseNode caseNode: {
                    // the subject comes first: a variable it declares is visible in the whens
                    var subject = caseNode.Predicate != null ? Expr(caseNode.Predicate) : null;
                    var whens = new List<WhenClause>();
                    foreach (var condition in caseNode.Conditions) {
                        var when = (Pm.WhenNode)condition;
                        var comparisons = new List<Expression>();
                        foreach (var comparison in when.Conditions) comparisons.Add(Argument(comparison));
                        whens.Add(new WhenClause(comparisons.ToArray(), BuildStatements(when.Statements), Span(when)));
                    }
                    Statements elseStatements = null;
                    if (caseNode.ElseClause is Pm.ElseNode caseElse) {
                        elseStatements = BuildStatements(caseElse.Statements);
                    }
                    return new CaseExpression(subject, whens.ToArray(), elseStatements, span);
                }

                case Pm.BeginNode begin: return BuildBeginBody(begin, span, null);
                case Pm.RescueModifierNode rescueMod:
                    return new RescueExpression(Expr(rescueMod.Expression), Expr(rescueMod.RescueExpression),
                        Span(rescueMod.RescueExpression), span);

                case Pm.DefinedNode defined:
                    return Defined(defined.Value, span);

                case Pm.CaseMatchNode caseMatch: return CaseMatch(caseMatch, span);
                case Pm.MatchPredicateNode matchPredicate: {
                    // value in pattern  =>  true/false
                    Expression assign;
                    var temp = NewTemp(Expr(matchPredicate.Value), span, out assign);
                    var enclosingMissingKey = _patternMissingKey;
                    var enclosingFailure = _patternFailure;
                    _patternMissingKey = null;
                    _patternFailure = null;
                    try {
                        return new BlockExpression(MakeStatements(new Expression[] {
                            assign,
                            new ConditionalExpression(PatternTest(matchPredicate.Pattern, temp, span),
                                Literal.True(span), Literal.False(span), span)
                        }), span);
                    } finally {
                        _patternMissingKey = enclosingMissingKey;
                        _patternFailure = enclosingFailure;
                    }
                }
                case Pm.MatchRequiredNode matchRequired: {
                    // value => pattern  =>  nil, raises NoMatchingPatternError on mismatch
                    Expression assign;
                    var temp = NewTemp(Expr(matchRequired.Value), span, out assign);
                    var enclosingMissingKey = _patternMissingKey;
                    var enclosingFailure = _patternFailure;
                    _patternMissingKey = CurrentScope.ResolveOrAddVariable("?pmk" + _tempCounter++ + "?", span);
                    _patternFailure = CurrentScope.ResolveOrAddVariable("?pmf" + _tempCounter++ + "?", span);
                    try {
                        return new BlockExpression(MakeStatements(new Expression[] {
                            assign,
                            new UnlessExpression(ResetMissingKey(PatternTest(matchRequired.Pattern, temp, span), span),
                                new Statements(RaiseNoMatchingPattern(temp, span)), null, span)
                        }), span);
                    } finally {
                        _patternMissingKey = enclosingMissingKey;
                        _patternFailure = enclosingFailure;
                    }
                }

                case Pm.BackReferenceReadNode backRef: {
                    switch (backRef.Name.Substring(1)) {
                        case "&": return new RegexMatchReference(0, span);
                        case "~": return new RegexMatchReference(-1, span);
                        case "+": return new RegexMatchReference(-2, span);
                        case "`": return new RegexMatchReference(-3, span);
                        case "'": return new RegexMatchReference(-4, span);
                        default: throw Unsupported(node);
                    }
                }
                case Pm.NumberedReferenceReadNode numberedRef:
                    // prism reports 0 for a number too big to be one (and has warned); MRI reads it as nil
                    if (numberedRef.Number == 0 || numberedRef.Number > int.MaxValue) {
                        return Literal.Nil(span);
                    }
                    return new RegexMatchReference((int)numberedRef.Number, span);
                case Pm.MatchWriteNode matchWrite: {
                    var call = (Pm.CallNode)matchWrite.Call;
                    if (!(Expr(call.Receiver) is RegularExpression regex)) throw Unsupported(node);
                    var locals = new List<LocalVariable>();
                    foreach (var target in matchWrite.Targets) {
                        locals.Add(CurrentScope.ResolveOrAddVariable(((Pm.LocalVariableTargetNode)target).Name, Span(target)));
                    }
                    var arguments = (Pm.ArgumentsNode)call.Arguments;
                    // /(?<name>..)/ =~ str assigns every named group to a local: the group's text,
                    // or nil when the group did not take part or the match failed
                    Expression matchAssign;
                    var matchResult = NewTemp(new MatchExpression(regex, Expr(arguments.Arguments[0]), span), span, out matchAssign);
                    var statements = new List<Expression> { matchAssign };
                    foreach (var local in locals) {
                        statements.Add(new SimpleAssignmentExpression(local,
                            new AndExpression(new RegexMatchReference(-1, span),
                                new MethodCall(new RegexMatchReference(-1, span), "[]",
                                    new Arguments(new SymbolLiteral(local.Name, _encoding, span)), span), span),
                            null, span));
                    }
                    statements.Add(matchResult);
                    return new BlockExpression(MakeStatements(statements.ToArray()), span);
                }

                case Pm.MultiWriteNode multiWrite: {
                    // MRI evaluates everything the l-values need - receivers and index
                    // arguments, left to right - before the right hand side.
                    var hoist = new Statements();
                    var lhs = CompoundTarget(multiWrite.Lefts, multiWrite.Rest, multiWrite.Rights, hoist);
                    var assignment = new ParallelAssignmentExpression(lhs, RhsFromValue(multiWrite.Value), span);
                    if (hoist.Count == 0) {
                        return assignment;
                    }
                    hoist.Add(assignment);
                    return new BlockExpression(hoist, span);
                }

                case Pm.CallOperatorWriteNode callOp:
                    return MemberOpAssign(callOp, callOp.Receiver, callOp.ReadName, callOp.BinaryOperator, callOp.Value, span);
                case Pm.CallOrWriteNode callOr:
                    return MemberOpAssign(callOr, callOr.Receiver, callOr.ReadName, "||", callOr.Value, span);
                case Pm.CallAndWriteNode callAnd:
                    return MemberOpAssign(callAnd, callAnd.Receiver, callAnd.ReadName, "&&", callAnd.Value, span);

                case Pm.IndexOperatorWriteNode indexOp:
                    return IndexOperatorAssignment(Expr(indexOp.Receiver), indexOp.Arguments,
                        Expr(indexOp.Value), indexOp.BinaryOperator, span);
                case Pm.IndexOrWriteNode indexOr:
                    return IndexOperatorAssignment(Expr(indexOr.Receiver), indexOr.Arguments,
                        Expr(indexOr.Value), "||", span);
                case Pm.IndexAndWriteNode indexAnd:
                    return IndexOperatorAssignment(Expr(indexAnd.Receiver), indexAnd.Arguments,
                        Expr(indexAnd.Value), "&&", span);

                case Pm.DefNode def: return Def(def, span);
                case Pm.ClassNode classNode: return Class(classNode, span);
                case Pm.ModuleNode module: return Module(module, span);
                case Pm.SingletonClassNode singleton: {
                    // the singleton expression (class << expr) is evaluated in the outer scope
                    var singletonExpr = Expr(singleton.Expression);
                    var scope = new TopLocalDefinitionLexicalScope(CurrentScope);
                    _scopes.Push(scope);
                    try {
                        return new SingletonDefinition(scope, singletonExpr, DefinitionBody(singleton.Body, span, null), span);
                    } finally {
                        _scopes.Pop();
                    }
                }
                case Pm.LambdaNode lambda: {
                    var scope = new BlockLexicalScope(CurrentScope);
                    _scopes.Push(scope);
                    try {
                        Statements prologue;
                        var parameters = BlockParameters(lambda.Parameters, false, out prologue);
                        var body = BlockBody(lambda.Body, span, prologue);
                        return new LambdaDefinition(new BlockDefinition(scope, parameters, body, span));
                    } finally {
                        _scopes.Pop();
                    }
                }

                case Pm.ReturnNode ret:
                    if (ret.Arguments != null && !_isEval && CurrentScope is TopStaticLexicalScope &&
                        _errorSink != null && _sourceUnit != null) {
                        _errorSink.Add(_sourceUnit, "argument of top-level return is ignored", span,
                            Errors.RuntimeWarning, Severity.Warning);
                    }
                    return new ReturnStatement(OptionalArguments(ret.Arguments), span);
                case Pm.BreakNode brk:
                    return new BreakStatement(OptionalArguments(brk.Arguments), span);
                case Pm.NextNode next:
                    return new NextStatement(OptionalArguments(next.Arguments), span);
                case Pm.RetryNode _: return new RetryStatement(span);
                case Pm.RedoNode _: return new RedoStatement(span);
                case Pm.YieldNode yield:
                    _usesBlock = true;
                    if (!IsYieldAllowed() && _errorSink != null) {
                        _errorSink.Add(_sourceUnit, "Invalid yield", span, 0, Severity.FatalError);
                    }
                    return new YieldCall(yield.Arguments != null ? BuildArguments(yield.Arguments) : null, span);

                case Pm.SuperNode super: {
                    if (super.Block == null) {
                        // passes the method's own block on
                        _usesBlock = true;
                    }
                    // Arguments before the block, for the reason given in Call().
                    Block superBlock = null;
                    var superArgs = super.Arguments != null
                        ? BuildArguments(super.Arguments, ref superBlock)
                        : new Arguments();
                    Block superLiteralBlock = OptionalBlock(super.Block);
                    if (superLiteralBlock != null) {
                        superBlock = superLiteralBlock;
                    }
                    return new SuperCall(superArgs, superBlock, span);
                }
                case Pm.ForwardingSuperNode forwardingSuper:
                    if (forwardingSuper.Block == null) {
                        _usesBlock = true;
                    }
                    return new SuperCall(_zsuperArguments,
                        forwardingSuper.Block != null ? BlockDef((Pm.BlockNode)forwardingSuper.Block) : null, span, true);

                case Pm.ForNode forNode: {
                    // index and collection belong to the outer scope; only the body is
                    // parsed inside the padding scope (matches the legacy grammar)
                    var target = Target(forNode.Index);
                    var clv = target as CompoundLeftValue ?? new CompoundLeftValue(new[] { target });
                    Parameters parameters = clv.HasUnsplattedValue
                        ? new Parameters(RemoveAt(clv.LeftValues, clv.UnsplattedValueIndex), clv.UnsplattedValueIndex,
                            null, clv.UnsplattedValue, null, SourceSpan.None)
                        : new Parameters(clv.LeftValues, clv.LeftValues.Length, null, null, null, SourceSpan.None);
                    var collection = Expr(forNode.Collection);
                    var forScope = new PaddingLexicalScope(CurrentScope);
                    _scopes.Push(forScope);
                    try {
                        return new ForLoopExpression(forScope, parameters, collection,
                            BuildStatements(forNode.Statements), span);
                    } finally {
                        _scopes.Pop();
                    }
                }

                case Pm.AliasMethodNode aliasMethod:
                    return new AliasStatement(true, Symbol(aliasMethod.NewName), Symbol(aliasMethod.OldName), span);
                case Pm.AliasGlobalVariableNode aliasGlobal:
                    return new AliasStatement(false,
                        new ConstructedSymbol(GlobalAliasName(aliasGlobal.NewName)),
                        new ConstructedSymbol(GlobalAliasName(aliasGlobal.OldName)), span);
                case Pm.UndefNode undef: {
                    var names = new List<ConstructedSymbol>();
                    foreach (var name in undef.Names) names.Add(Symbol(name));
                    return new UndefineStatement(names, span);
                }

                case Pm.PostExecutionNode postExec: {
                    var scope = new PaddingLexicalScope(CurrentScope);
                    _scopes.Push(scope);
                    try {
                        return new ShutdownHandlerStatement(scope, BuildStatements(postExec.Statements), span);
                    } finally {
                        _scopes.Pop();
                    }
                }
                case Pm.PreExecutionNode preExec:
                    // BEGIN{}: run inline (correct unless code precedes it textually, which is rare)
                    return StatementsAsExpression(preExec.Statements, span);

                case Pm.SourceFileNode sourceFile:
                    return new StringLiteral(_path ?? LiteralText(sourceFile.Filepath), _encoding, span);
                case Pm.SourceLineNode _:
                    return Literal.Integer(span.Start.Line + _lineOffset, span);
                case Pm.SourceEncodingNode _:
                    return new EncodingExpression(span);

                default:
                    throw Unsupported(node);
            }
        }

        /// <summary>
        /// prism hands back raw bytes. StringLiteral accepts either a string or a byte[],
        /// so keep the bytes whenever they are not valid UTF-8 — decoding those would
        /// replace them with U+FFFD and silently corrupt binary literals like "\xE3\xB0".
        /// </summary>
        private object/*!*/ LiteralValue(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            // A character representation is only lossless if the characters encode back to the
            // same bytes under the literal's own encoding. That holds for UTF-8 and for pure
            // ASCII; anything else (e.g. "\xE9\xA1\xB6" in a binary file, which happens to be
            // valid UTF-8) has to stay a byte string.
            if (encoding != RubyEncoding.UTF8) {
                foreach (byte b in bytes) {
                    if (b >= 0x80) {
                        return bytes;
                    }
                }
            }

            try {
                return _strictUtf8.GetString(bytes);
            } catch (System.Text.DecoderFallbackException) {
                return bytes;
            }
        }

        /// <summary>
        /// A symbol's characters have to be read back through its own encoding. The bytes
        /// \xC3\xA9 are one character in a UTF-8 file and two in a binary one, and reading them
        /// as UTF-8 either way loses that - the symbol would come out a byte shorter than the
        /// source asked for.
        /// </summary>
        private string/*!*/ LiteralText(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            try {
                return encoding.StrictEncoding.GetString(bytes);
            } catch (System.Text.DecoderFallbackException) {
                return LiteralText(bytes);
            }
        }

        private string/*!*/ LiteralText(byte[]/*!*/ bytes) {
            // for places that can only hold a string (symbols, file names)
            try {
                return _strictUtf8.GetString(bytes);
            } catch (System.Text.DecoderFallbackException) {
                return System.Text.Encoding.Latin1.GetString(bytes);
            }
        }

        private static readonly System.Text.Encoding _strictUtf8 =
            new System.Text.UTF8Encoding(false, true);

        // pm_string_flags / pm_symbol_flags (node-specific flags start at bit 2)
        private const uint PmForcedUtf8Encoding = 4;
        private const uint PmForcedBinaryEncoding = 8;
        private const uint PmSymbolForcedUsAsciiEncoding = 16;

        /// <summary>
        /// The encoding of a string/symbol literal. Normally it is the source encoding, but a
        /// literal that contains a \u escape is UTF-8 whatever the file says, and one that
        /// contains a \x byte above 0x7f in a US-ASCII file is binary. prism has already worked
        /// this out and reports it in the node flags; CRuby uses exactly the same rule.
        /// </summary>
        private RubyEncoding/*!*/ LiteralEncoding(Pm.PmNode/*!*/ node) {
            uint flags = node.Flags;
            if ((flags & PmForcedUtf8Encoding) != 0) {
                return RubyEncoding.UTF8;
            }
            if ((flags & PmForcedBinaryEncoding) != 0) {
                return RubyEncoding.Binary;
            }
            return _encoding;
        }

        private RubyEncoding/*!*/ SymbolEncoding(Pm.PmNode/*!*/ node) {
            if ((node.Flags & PmSymbolForcedUsAsciiEncoding) != 0) {
                return RubyEncoding.Ascii;
            }
            return LiteralEncoding(node);
        }

        /// <summary>
        /// MRI compiles a regexp literal along with the code around it, so Onigmo's warnings about
        /// the pattern come when the file is compiled, whether or not the literal is ever reached.
        /// </summary>
        private void WarnRegexp(byte[]/*!*/ pattern, SourceSpan span) {
            if (_errorSink == null || _sourceUnit == null) {
                return;
            }
            string text = System.Text.Encoding.UTF8.GetString(pattern);
            if (!MayHaveNestedQuantifier(text)) {
                return;
            }
            var warnings = RegexpTransformer.GetWarnings(text);
            if (warnings != null) {
                foreach (var warning in warnings) {
                    _errorSink.Add(_sourceUnit, warning + ": /" + text + "/", span, Errors.RuntimeWarning, Severity.Warning);
                }
            }
        }

        /// <summary>
        /// The only warnings RegexpTransformer reports are about a quantifier applied directly to
        /// another (`a**`, `a{2}*`, `a*{0,1}`), which needs two quantifier characters in a row. Whitespace
        /// or a comment may sit between them in extended mode, so a pattern with either is always checked.
        /// </summary>
        private static bool MayHaveNestedQuantifier(string/*!*/ pattern) {
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '#' || Char.IsWhiteSpace(c)) {
                    return true;
                }
                if ((c == '?' || c == '*' || c == '+' || c == '}') && i + 1 < pattern.Length) {
                    char d = pattern[i + 1];
                    if (d == '?' || d == '*' || d == '+' || d == '{') {
                        return true;
                    }
                }
            }
            return false;
        }

        private Expression/*!*/ BigIntegerLiteral(BigInteger value, SourceSpan span) {
            // An Integer is an Int32 if it fits in one, otherwise an Int64 if it fits in one (see
            // Protocols.Normalize): a BigInteger literal in the Int64 range would not be eql? to the
            // same value computed at run time, nor find it as a Hash key.
            if (value >= int.MinValue && value <= int.MaxValue) return Literal.Integer((int)value, span);
            if (value >= long.MinValue && value <= long.MaxValue) return Literal.Long((long)value, span);
            return Literal.BigInteger(value, span);
        }

        private string/*!*/ GlobalAliasName(Pm.PmNode/*!*/ node) {
            switch (node) {
                case Pm.GlobalVariableReadNode gvar: return gvar.Name.Substring(1);
                case Pm.BackReferenceReadNode backRef: return backRef.Name.Substring(1);
                case Pm.NumberedReferenceReadNode numbered: return numbered.Number.ToString();
                default: throw Unsupported(node);
            }
        }

        private List<Expression>/*!*/ StringParts(Pm.PmNode[]/*!*/ parts) {
            var result = new List<Expression>();
            foreach (var part in parts) {
                switch (part) {
                    case Pm.StringNode str:
                        result.Add(new StringLiteral(LiteralValue(str.Unescaped, LiteralEncoding(str)), LiteralEncoding(str), Span(str)));
                        break;
                    case Pm.EmbeddedStatementsNode embedded:
                        result.Add(StatementsAsExpression(embedded.Statements, Span(embedded)));
                        break;
                    case Pm.EmbeddedVariableNode embeddedVar:
                        result.Add(Expr(embeddedVar.Variable));
                        break;
                    default:
                        result.Add(Expr(part)); // nested interpolations, heredocs
                        break;
                }
            }
            return result;
        }

        private ConstructedSymbol Symbol(Pm.PmNode/*!*/ node) {
            switch (node) {
                case Pm.SymbolNode symbol: return new ConstructedSymbol(LiteralText(symbol.Unescaped));
                case Pm.InterpolatedSymbolNode interp:
                    return new ConstructedSymbol((StringConstructor)Expr(interp));
                default: throw Unsupported(node);
            }
        }

        private RubyRegexOptions RegexOptions(Pm.PmNode/*!*/ node) {
            var options = RubyRegexOptions.NONE;
            if (HasFlag(node, Pm.RegularExpressionFlags.IgnoreCase)) options |= RubyRegexOptions.IgnoreCase;
            if (HasFlag(node, Pm.RegularExpressionFlags.Extended)) options |= RubyRegexOptions.Extended;
            if (HasFlag(node, Pm.RegularExpressionFlags.MultiLine)) options |= RubyRegexOptions.Multiline;
            if (HasFlag(node, Pm.RegularExpressionFlags.Once)) options |= RubyRegexOptions.Once;
            if (HasFlag(node, Pm.RegularExpressionFlags.EucJp)) options |= RubyRegexOptions.EUC;
            if (HasFlag(node, Pm.RegularExpressionFlags.Windows31j)) options |= RubyRegexOptions.SJIS;
            if (HasFlag(node, Pm.RegularExpressionFlags.Utf8)) options |= RubyRegexOptions.UTF8;
            if (HasFlag(node, Pm.RegularExpressionFlags.Ascii8bit)) options |= RubyRegexOptions.FIXED;
            return options;
        }

        private ConstantVariable/*!*/ ConstantPath(Pm.PmNode/*!*/ node, SourceSpan span) {
            switch (node) {
                case Pm.ConstantReadNode read:
                    return new ConstantVariable(IdentifierText(read.Name), span);
                case Pm.ConstantPathNode path when path.Name != null:
                    // null parent = ::Foo, explicitly bound to Object
                    return new ConstantVariable(path.Parent != null ? Expr(path.Parent) : null, IdentifierText(path.Name), span);
                case Pm.ConstantPathTargetNode target when target.Name != null:
                    return new ConstantVariable(target.Parent != null ? Expr(target.Parent) : null, IdentifierText(target.Name), span);
                default:
                    throw Unsupported(node);
            }
        }

        // ---- defined? ----

        // `defined?((x))` classifies like `defined?(x)` in CRuby, but `defined?((a; b))`
        // is just an "expression", so only a single-statement body is unwrapped.
        private static Pm.PmNode/*!*/ Unparen(Pm.PmNode/*!*/ node) {
            while (true) {
                Pm.PmNode body;
                if (node is Pm.ParenthesesNode parens) {
                    body = parens.Body;
                } else if (node is Pm.StatementsNode) {
                    body = node;
                } else {
                    return node;
                }

                var statements = body as Pm.StatementsNode;
                if (statements == null || statements.Body.Length != 1) return node;
                node = statements.Body[0];
            }
        }

        // defined? answers with a frozen String in CRuby; IsDefinedExpression builds a mutable one.
        /// <summary>
        /// prism flags a literal Frozen where frozen_string_literal is on and Mutable where it was
        /// explicitly turned off. Neither flag means the file said nothing, which since Ruby 3.4
        /// is not the same as saying false: the literal is chilled, mutable but warning the first
        /// time it is mutated.
        /// </summary>
        /// <summary>
        /// The one literal behind a string written as several adjacent ones, or null when the
        /// node really does interpolate something - or holds bytes rather than characters, where
        /// concatenating the pieces is not this layer's job.
        /// </summary>
        private Expression TryFoldAdjacentLiterals(Pm.InterpolatedStringNode/*!*/ node, SourceSpan span) {
            if (node.Parts == null || node.Parts.Length == 0) {
                return null;
            }

            RubyEncoding encoding = null;
            var text = new System.Text.StringBuilder();
            foreach (var part in node.Parts) {
                var str = part as Pm.StringNode;
                if (str == null) {
                    return null;
                }

                var partEncoding = LiteralEncoding(str);
                if (encoding == null) {
                    encoding = partEncoding;
                } else if (encoding != partEncoding) {
                    return null;
                }

                var value = LiteralValue(str.Unescaped, partEncoding) as string;
                if (value == null) {
                    return null;
                }
                text.Append(value);
            }

            var mutability = HasFlag(node, Pm.InterpolatedStringNodeFlags.Frozen)
                ? StringLiteralMutability.Frozen
                : HasFlag(node, Pm.InterpolatedStringNodeFlags.Mutable)
                    ? StringLiteralMutability.Mutable
                    : StringLiteralMutability.Chilled;

            return new StringLiteral(text.ToString(), encoding, mutability, span);
        }

        private static StringLiteralMutability StringMutability(Pm.PmNode/*!*/ node) {
            if (HasFlag(node, Pm.StringFlags.Frozen)) {
                return StringLiteralMutability.Frozen;
            }
            return HasFlag(node, Pm.StringFlags.Mutable)
                ? StringLiteralMutability.Mutable : StringLiteralMutability.Chilled;
        }

        private Expression/*!*/ Freeze(Expression/*!*/ value, SourceSpan span) {
            return new MethodCall(value, "freeze", null, span);
        }

        private Expression/*!*/ FrozenString(string/*!*/ text, SourceSpan span) {
            return Freeze(new StringLiteral(text, RubyEncoding.Binary, span), span);
        }

        // <test> ? "<category>".freeze : nil -- used where IsDefinedExpression would report
        // the wrong category for the node the construct was lowered to.
        private Expression/*!*/ DefinedAs(Expression/*!*/ test, string/*!*/ category, SourceSpan span) {
            return new ConditionalExpression(test, FrozenString(category, span), Literal.Nil(span), span);
        }

        private Expression/*!*/ Defined(Pm.PmNode/*!*/ value, SourceSpan span) {
            var node = Unparen(value);

            switch (node) {
                // Since 3.4 every op-assignment answers "assignment" no matter what the target is.
                // `x ||= v` lowers to a SimpleAssignmentExpression whose "||" operation makes the
                // 1.9 AST report "expression" instead.
                case Pm.LocalVariableOrWriteNode _:
                case Pm.LocalVariableAndWriteNode _:
                case Pm.InstanceVariableOrWriteNode _:
                case Pm.InstanceVariableAndWriteNode _:
                case Pm.ClassVariableOrWriteNode _:
                case Pm.ClassVariableAndWriteNode _:
                case Pm.GlobalVariableOrWriteNode _:
                case Pm.GlobalVariableAndWriteNode _:
                case Pm.ConstantOrWriteNode _:
                case Pm.ConstantAndWriteNode _:
                case Pm.ConstantPathOrWriteNode _:
                case Pm.ConstantPathAndWriteNode _:
                case Pm.IndexOrWriteNode _:
                case Pm.IndexAndWriteNode _:
                case Pm.CallOrWriteNode _:
                case Pm.CallAndWriteNode _:
                case Pm.IndexOperatorWriteNode _:
                case Pm.CallOperatorWriteNode _:
                    return DefinedAs(new IsDefinedExpression(Expr(node), span), "assignment", span);

                // an array literal is only defined if every element is
                case Pm.ArrayNode array:
                    return DefinedAs(ElementsDefined(array, span), "expression", span);

                // `a.x = v` and `a[i] = v` are the x= / []= methods, not assignments; they lower
                // to a SimpleAssignmentExpression, which would report "assignment"
                case Pm.CallNode attributeWrite
                    when attributeWrite.Receiver != null
                        && HasFlag(attributeWrite, Pm.CallNodeFlags.AttributeWrite)
                        && !HasFlag(attributeWrite, Pm.CallNodeFlags.SafeNavigation):
                    return GuardedDefined(attributeWrite.Receiver,
                        new IsDefinedExpression(
                            new MethodCall(Expr(attributeWrite.Receiver), attributeWrite.Name, null, span), span),
                        span);

                case Pm.CallNode call when call.Receiver != null:
                    return GuardedDefined(call.Receiver, new IsDefinedExpression(Expr(node), span), span);

                case Pm.ConstantPathNode path when path.Parent != null:
                    return GuardedDefined(path.Parent, new IsDefinedExpression(Expr(node), span), span);

                default:
                    return Freeze(new IsDefinedExpression(Expr(node), span), span);
            }
        }

        // CRuby requires the receiver of a call (and the qualifier of a constant path) to be
        // defined before it evaluates it, so `!$never_assigned` is nil rather than "method".
        // MethodCall/ConstantVariable only guard against the receiver *raising*.
        private Expression/*!*/ GuardedDefined(Pm.PmNode/*!*/ receiver, Expression/*!*/ defined, SourceSpan span) {
            var result = Freeze(defined, span);
            var guard = DefinednessGuard(receiver, span);
            return guard == null ? result : new ConditionalExpression(guard, result, Literal.Nil(span), span);
        }

        // A test for "reading this receiver is defined", or null when reading it either always
        // is, or can only fail by raising -- which the lowered node already handles.
        private Expression DefinednessGuard(Pm.PmNode/*!*/ receiver, SourceSpan span) {
            var node = Unparen(receiver);
            switch (node) {
                case Pm.GlobalVariableReadNode _:
                case Pm.InstanceVariableReadNode _:
                case Pm.ClassVariableReadNode _:
                case Pm.BackReferenceReadNode _:
                case Pm.NumberedReferenceReadNode _:
                case Pm.ConstantReadNode _:
                case Pm.ConstantPathNode _:
                    return new IsDefinedExpression(Expr(node), span);
                default:
                    return null;
            }
        }

        private Expression/*!*/ ElementsDefined(Pm.ArrayNode/*!*/ node, SourceSpan span) {
            Expression result = null;
            foreach (var element in node.Elements) {
                var value = element is Pm.SplatNode splat ? splat.Expression : element;
                if (value == null) continue;
                var test = new IsDefinedExpression(Expr(Unparen(value)), Span(value));
                result = (result == null) ? (Expression)test : new AndExpression(result, test, span);
            }
            return result ?? (Expression)Literal.True(span);
        }

        private Expression/*!*/ If(Pm.IfNode/*!*/ node, SourceSpan span) {
            // Built in source order, and not as arguments to the IfExpression constructor:
            // /(?<x>..)/ =~ s introduces the local x where it appears, so a bare `x` in an
            // earlier branch is still a method call and one in a later branch is not.
            // Walking the else branch first made the whole statement see the local.
            var condition = Condition(node.Predicate);
            var body = BuildStatements(node.Statements);

            var elseIfClauses = new List<ElseIfClause>();
            var subsequent = node.Subsequent;
            while (subsequent != null) {
                if (subsequent is Pm.IfNode elseIf) {
                    elseIfClauses.Add(new ElseIfClause(Condition(elseIf.Predicate), BuildStatements(elseIf.Statements), Span(elseIf)) { IsElsif = true });
                    subsequent = elseIf.Subsequent;
                } else {
                    var elseNode = (Pm.ElseNode)subsequent;
                    elseIfClauses.Add(new ElseIfClause(null, BuildStatements(elseNode.Statements), Span(elseNode)));
                    break;
                }
            }
            return new IfExpression(condition, body, elseIfClauses, span);
        }

        private Expression/*!*/ HashExpression(Pm.PmNode[]/*!*/ elements, bool isKeywordArguments, SourceSpan span) {
            // fold AssocNode runs and ** splats into successive Hash#merge calls
            Expression result = null;
            var maplets = new List<Maplet>();

            foreach (var element in elements) {
                switch (element) {
                    case Pm.AssocNode assoc:
                        maplets.Add(new Maplet(Expr(assoc.Key), Expr(assoc.Value), Span(assoc)));
                        break;
                    case Pm.AssocSplatNode splat when isKeywordArguments:
                        result = MergeKeywordSplat(MergeHash(result, maplets, isKeywordArguments, span) ?? new HashConstructor(new Maplet[0], true, span),
                            splat.Value != null ? Expr(splat.Value) : AnonymousParameter(AnonymousKeywordRestName, splat), span);
                        break;
                    case Pm.AssocSplatNode splat:
                        result = MergeHash(result, maplets, isKeywordArguments, span);
                        // `**nil` contributes nothing (Ruby 3.4); it is only known at run time
                        var splatted = new OrExpression(splat.Value != null ? Expr(splat.Value) : AnonymousParameter(AnonymousKeywordRestName, splat),
                            new HashConstructor(new Maplet[0], span), span);
                        // always merge rather than pass the splatted object along: Hash#merge is
                        // what applies the #to_hash protocol to it
                        result = new MethodCall(result ?? new HashConstructor(new Maplet[0], isKeywordArguments, span),
                            "merge", new Arguments(splatted), span);
                        break;
                    default:
                        throw Unsupported(element);
                }
            }
            result = MergeHash(result, maplets, isKeywordArguments, span);
            return result ?? new HashConstructor(new Maplet[0], isKeywordArguments, span);
        }

        private Expression MergeHash(Expression result, List<Maplet>/*!*/ maplets, bool isKeywordArguments, SourceSpan span) {
            if (maplets.Count == 0) return result;
            var ctor = new HashConstructor(maplets.ToArray(), isKeywordArguments, span);
            maplets.Clear();
            if (result != null && isKeywordArguments) {
                return MergeKeywordSplat(result, ctor, span);
            }
            return result == null ? (Expression)ctor : new MethodCall(result, "merge", new Arguments(ctor), span);
        }

        /// <summary>
        /// `hash.merge(splatted || {})` for keyword arguments, where the hash is the call's own: a
        /// Hash (or nil) is copied into it in place, and only anything else takes the merge, which
        /// applies #to_hash - see KeywordSplatMerge.
        /// </summary>
        private Expression/*!*/ MergeKeywordSplat(Expression/*!*/ hash, Expression/*!*/ splatted, SourceSpan span) {
            var hashTemp = NewTemp(hash, span, out var hashAssignment);
            var splatTemp = NewTemp(splatted, span, out var splatAssignment);
            return new KeywordSplatMerge(hashAssignment, splatAssignment,
                new MethodCall(hashTemp, "merge", new Arguments(
                    new OrExpression(splatTemp, new HashConstructor(new Maplet[0], span), span)), span), span);
        }

        // ---- calls ----

        private Expression/*!*/ Call(Pm.CallNode/*!*/ node, SourceSpan span) {
            string name = node.Name;

            // prism can't see locals defined outside an eval'd unit unless scopes are
            // passed; resolve VARIABLE_CALL bare words against the scope chain first
            if (HasFlag(node, Pm.CallNodeFlags.VariableCall)) {
                var local = CurrentScope.ResolveVariable(name);
                if (local != null) return local;
            }

            if (HasFlag(node, Pm.CallNodeFlags.SafeNavigation)) {
                return SafeNavigation(node, span);
            }

            if (HasFlag(node, Pm.CallNodeFlags.AttributeWrite)) {
                return AttributeWrite(node, Expr(node.Receiver), name, span);
            }

            Expression receiver = node.Receiver != null ? Expr(node.Receiver) : null;
            // The arguments are walked before the block: a local first assigned in the argument
            // list belongs to the enclosing scope, and the block that follows closes over that
            // local rather than declaring a fresh one - `h.fetch(k = key) { p k }` prints the key.
            // Building the block first would resolve `k` against a scope that has not seen the
            // assignment yet, and the block would read a nil of its own.
            Block block = null;
            Arguments args = node.Arguments != null ? BuildArguments(node.Arguments, ref block) : null;
            Block literalBlock = OptionalBlock(node.Block);
            if (literalBlock != null) {
                block = literalBlock;
            }
            return new MethodCall(receiver, name, args, block, span) {
                IsVariableCall = HasFlag(node, Pm.CallNodeFlags.VariableCall)
            };
        }

        /// <summary>
        /// `target.x = v` and `target[i] = v` are assignments rather than plain calls to x= and
        /// []=: what they answer is v, whatever the method itself returns.
        /// </summary>
        private Expression/*!*/ AttributeWrite(Pm.CallNode/*!*/ node, Expression/*!*/ target, string/*!*/ name, SourceSpan span) {
            var writeArgs = ((Pm.ArgumentsNode)node.Arguments).Arguments;
            var rhs = Expr(writeArgs[writeArgs.Length - 1]);
            LeftValue lhs;
            if (name == "[]=") {
                var indexArgs = new List<Expression>();
                for (int i = 0; i < writeArgs.Length - 1; i++) indexArgs.Add(Argument(writeArgs[i]));
                lhs = new ArrayItemAccess(target, new Arguments(indexArgs.ToArray()), null, span);
            } else {
                lhs = new AttributeAccess(target, name.TrimEnd('='), span);
            }
            return new SimpleAssignmentExpression(lhs, rhs, null, span);
        }

        /// <summary>
        /// `recv.m op= v`, and its safe form `recv&.m op= v`, which does nothing at all when the
        /// receiver is nil rather than reading m from it.
        /// </summary>
        private Expression/*!*/ MemberOpAssign(Pm.PmNode/*!*/ node, Pm.PmNode/*!*/ receiver, string/*!*/ name,
            string/*!*/ op, Pm.PmNode/*!*/ value, SourceSpan span) {

            if (HasFlag(node, Pm.CallNodeFlags.SafeNavigation)) {
                return SafeGuard(receiver, temp => new MemberAssignmentExpression(temp, name, op, Expr(value), span), span);
            }

            return new MemberAssignmentExpression(Expr(receiver), name, op, Expr(value), span);
        }

        private Expression/*!*/ SafeNavigation(Pm.CallNode/*!*/ node, SourceSpan span) {
            // self&.m: self is never nil, and the call keeps the right of `self.m` to reach a
            // private method - a temporary standing in for self would lose it
            if (node.Receiver is Pm.SelfNode) {
                return HasFlag(node, Pm.CallNodeFlags.AttributeWrite)
                    ? AttributeWrite(node, new SelfReference(span), node.Name, span)
                    : new MethodCall(new SelfReference(span), node.Name,
                        node.Arguments != null ? BuildArguments(node.Arguments) : null, OptionalBlock(node.Block), span);
            }

            // recv&.m(args)  =>  (?safeN? = recv).nil? ? nil : ?safeN?.m(args)
            return SafeGuard(node.Receiver, temp =>
                HasFlag(node, Pm.CallNodeFlags.AttributeWrite)
                    ? AttributeWrite(node, temp, node.Name, span)
                    : new MethodCall(temp, node.Name,
                        node.Arguments != null ? BuildArguments(node.Arguments) : null, OptionalBlock(node.Block), span),
                span);
        }

        /// <summary>
        /// The guard every form of `&.` shares: the receiver is read once, and nothing at all
        /// happens when it is nil - not the call, and for `recv&.m op= v` not even the read.
        /// </summary>
        private Expression/*!*/ SafeGuard(Pm.PmNode/*!*/ receiver, Func<Expression/*!*/, Expression/*!*/>/*!*/ body, SourceSpan span) {
            var temp = CurrentScope.ResolveOrAddVariable("?safe" + _tempCounter++ + "?", span);
            var assign = new SimpleAssignmentExpression(temp, Expr(receiver), null, span);
            var test = new MethodCall(assign, "nil?", null, span);
            return new ConditionalExpression(test, Literal.Nil(span), body(temp), span);
        }

        private Block OptionalBlock(Pm.PmNode block) {
            switch (block) {
                case null: return null;
                case Pm.BlockNode blockNode: return BlockDef(blockNode);
                case Pm.BlockArgumentNode blockArg when blockArg.Expression != null:
                    return new BlockReference(Expr(blockArg.Expression), Span(blockArg));
                case Pm.BlockArgumentNode anonymous: {
                    // `f(&)` forwards the anonymous block parameter of the enclosing `def f(&)`
                    var blockLocal = CurrentScope.ResolveVariable("?block?");
                    if (blockLocal == null) throw Unsupported(anonymous);
                    return new BlockReference(blockLocal, Span(anonymous));
                }
                default: throw Unsupported(block);
            }
        }

        private Expression/*!*/ Argument(Pm.PmNode/*!*/ node) {
            if (node is Pm.SplatNode splat) {
                // `f(*)` passes on the anonymous rest parameter of the enclosing `def m(*)`
                return new SplattedArgument(splat.Expression != null ? Expr(splat.Expression) : AnonymousParameter(Symbols.RestArgsLocal, splat));
            }
            return Expr(node);
        }

        // `*` and `**` as arguments forward what the enclosing method's anonymous rest and keyword rest
        // parameters took, which the method keeps in hidden locals.
        private Expression/*!*/ AnonymousParameter(string/*!*/ name, Pm.PmNode/*!*/ node) {
            var local = CurrentScope.ResolveVariable(name);
            if (local == null) throw Unsupported(node);
            return local;
        }

        private Arguments/*!*/ BuildArguments(Pm.PmNode argumentsNode) {
            Block ignored = null;
            return BuildArguments(argumentsNode, ref ignored);
        }

        /// <summary>
        /// Builds the argument list, expanding `...` forwarding into the hidden rest local
        /// (and, when the caller passes no block of its own, the hidden block local).
        /// </summary>
        private Arguments/*!*/ BuildArguments(Pm.PmNode argumentsNode, ref Block block) {
            if (argumentsNode == null) return new Arguments();
            var arguments = ((Pm.ArgumentsNode)argumentsNode).Arguments;
            // each argument maps to exactly one expression
            var exprs = new Expression[arguments.Length];
            for (int i = 0; i < arguments.Length; i++) {
                var arg = arguments[i];
                if (arg is Pm.ForwardingArgumentsNode) {
                    var rest = CurrentScope.ResolveVariable(ForwardingRestName);
                    var fwdBlock = CurrentScope.ResolveVariable(ForwardingBlockName);
                    if (rest == null) throw Unsupported(arg);
                    exprs[i] = new SplattedArgument(rest);
                    if (block == null && fwdBlock != null) {
                        block = new BlockReference(fwdBlock, Span(arg));
                    }
                } else if (arg is Pm.KeywordHashNode single && single.Elements.Length == 1 &&
                    single.Elements[0] is Pm.AssocSplatNode singleSplat) {
                    // `f(**opts)`: a Hash (or nil) is splatted as it is; only anything else goes
                    // through the `{}.merge(opts)` HashExpression would build, for #to_hash
                    var span = Span(arg);
                    var temp = NewTemp(singleSplat.Value != null ? Expr(singleSplat.Value) : AnonymousParameter(AnonymousKeywordRestName, singleSplat),
                        span, out var assignment);
                    exprs[i] = new KeywordArgumentSplat(assignment, new MethodCall(new HashConstructor(new Maplet[0], true, span), "merge",
                        new Arguments(new OrExpression(temp, new HashConstructor(new Maplet[0], span), span)), span));
                } else if (IsSplattedKeywordHash(arg)) {
                    // `f(**opts)` must pass nothing when opts is empty - see KeywordArgumentSplat.
                    exprs[i] = new KeywordArgumentSplat(Argument(arg));
                } else {
                    exprs[i] = Argument(arg);
                }
            }
            return new Arguments(exprs);
        }

        /// <summary>
        /// True for a keyword-argument hash that was written with at least one `**` splat.
        /// `f(x: 1)` is excluded: it can never be empty, so it needs no run-time check.
        /// </summary>
        private static bool IsSplattedKeywordHash(Pm.PmNode/*!*/ node) {
            var keywordHash = node as Pm.KeywordHashNode;
            if (keywordHash == null) {
                return false;
            }
            foreach (var element in keywordHash.Elements) {
                if (element is Pm.AssocSplatNode) {
                    return true;
                }
            }
            return false;
        }

        private Arguments OptionalArguments(Pm.PmNode argumentsNode) {
            return argumentsNode != null ? BuildArguments(argumentsNode) : null;
        }

        // ---- targets / multiple assignment ----

        private LeftValue/*!*/ Target(Pm.PmNode/*!*/ node) {
            return Target(node, null);
        }

        /// <summary>
        /// An assignment target. When <paramref name="hoist"/> is given, everything the target
        /// needs in order to be written - its receiver and any index arguments - is evaluated
        /// into a temporary there and then, which is how a multiple assignment orders those
        /// against the right hand side.
        /// </summary>
        private LeftValue/*!*/ Target(Pm.PmNode/*!*/ node, Statements hoist) {
            // Nested targets ((a, b), c) recurse through here rather than through Expr (see StackGuard).
            if (!StackGuard.IsSufficient) {
                return StackGuard.RunOnFreshStack(() => Target(node, hoist));
            }
            var span = Span(node);
            switch (node) {
                case Pm.LocalVariableTargetNode local:
                    return CurrentScope.ResolveOrAddVariable(local.Name, span);
                case Pm.RequiredParameterNode requiredParam:
                    return CurrentScope.ResolveOrAddVariable(requiredParam.Name, span);
                case Pm.InstanceVariableTargetNode ivar:
                    return new InstanceVariable(IvarName(ivar.Name), span);
                case Pm.GlobalVariableTargetNode gvar:
                    return new IronRuby.Compiler.Ast.GlobalVariable(gvar.Name.Substring(1), span);
                case Pm.ClassVariableTargetNode cvar:
                    return new ClassVariable(cvar.Name, span);
                case Pm.ConstantTargetNode constant:
                    return new ConstantVariable(IdentifierText(constant.Name), span);
                case Pm.ConstantPathTargetNode constantPath:
                    return ConstantPath(constantPath, span);
                case Pm.IndexTargetNode index:
                    return new ArrayItemAccess(Hoist(Expr(index.Receiver), hoist, span),
                        HoistArguments(BuildArguments(index.Arguments), hoist, span), null, span);
                case Pm.CallTargetNode callTarget:
                    return new AttributeAccess(Hoist(Expr(callTarget.Receiver), hoist, span), callTarget.Name.TrimEnd('='), span) {
                        IsSafeNavigation = HasFlag(callTarget, Pm.CallNodeFlags.SafeNavigation)
                    };
                case Pm.MultiTargetNode multi:
                    return CompoundTarget(multi.Lefts, multi.Rest, multi.Rights, hoist);
                default:
                    throw Unsupported(node);
            }
        }

        private CompoundLeftValue/*!*/ CompoundTarget(Pm.PmNode[]/*!*/ lefts, Pm.PmNode rest, Pm.PmNode[]/*!*/ rights) {
            return CompoundTarget(lefts, rest, rights, null);
        }

        private CompoundLeftValue/*!*/ CompoundTarget(Pm.PmNode[]/*!*/ lefts, Pm.PmNode rest, Pm.PmNode[]/*!*/ rights, Statements hoist) {
            var lvs = new List<LeftValue>();
            foreach (var left in lefts) lvs.Add(Target(left, hoist));
            int unsplatIndex = int.MaxValue;
            if (rest != null) {
                unsplatIndex = lvs.Count;
                if (rest is Pm.SplatNode splat && splat.Expression != null) {
                    lvs.Add(Target(splat.Expression, hoist));
                } else {
                    lvs.Add(Placeholder.Singleton); // `a, * = x` / `a, = x`
                }
            }
            foreach (var right in rights) lvs.Add(Target(right, hoist));
            return unsplatIndex == int.MaxValue
                ? new CompoundLeftValue(lvs.ToArray())
                : new CompoundLeftValue(lvs.ToArray(), unsplatIndex);
        }

        /// <summary>Evaluates the expression into a temporary, appended to <paramref name="hoist"/>.</summary>
        private Expression/*!*/ Hoist(Expression/*!*/ expression, Statements hoist, SourceSpan span) {
            if (hoist == null || expression is Literal || expression is SelfReference) {
                return expression;
            }
            // ResolveOrAddVariable, not AddVariable: the name is unique, so nothing is ever
            // resolved, but inside a for-loop body CurrentScope is a PaddingLexicalScope,
            // which defines no variables of its own - the temp belongs to the scope around
            // the loop, and AddVariable would assert there.
            var temp = CurrentScope.ResolveOrAddVariable("?lhs" + _indexTempCount++ + "?", span);
            hoist.Add(new SimpleAssignmentExpression(temp, expression, null, span));
            return temp;
        }

        private Arguments/*!*/ HoistArguments(Arguments/*!*/ arguments, Statements hoist, SourceSpan span) {
            if (hoist == null || arguments.IsEmpty) {
                return arguments;
            }

            var expressions = arguments.Expressions;
            var hoisted = new Expression[expressions.Length];
            for (int i = 0; i < expressions.Length; i++) {
                // a splat is not a plain value and cannot be lifted out
                hoisted[i] = expressions[i] is SplattedArgument ? expressions[i] : Hoist(expressions[i], hoist, span);
            }
            return new Arguments(hoisted);
        }

        private Expression/*!*/[]/*!*/ RhsFromValue(Pm.PmNode/*!*/ value) {
            // prism wraps `a, b = 1, 2` in a synthesized ArrayNode (no bracket)
            if (value is Pm.ArrayNode array && array.OpeningLoc == null) {
                var rhs = new List<Expression>();
                foreach (var element in array.Elements) rhs.Add(Argument(element));
                return rhs.ToArray();
            }
            return new[] { Expr(value) };
        }

        // ---- bodies ----

        private Body/*!*/ BuildBeginBody(Pm.BeginNode/*!*/ node, SourceSpan span, Statements prologue) {
            var statements = BuildStatements(node.Statements);
            if (prologue != null) statements = Prepend(prologue, statements);

            List<RescueClause> rescues = null;
            var rescueNode = node.RescueClause as Pm.RescueNode;
            while (rescueNode != null) {
                if (rescues == null) rescues = new List<RescueClause>();
                rescues.Add(Rescue(rescueNode));
                rescueNode = rescueNode.Subsequent as Pm.RescueNode;
            }

            Statements elseStatements = null;
            if (node.ElseClause is Pm.ElseNode elseNode) elseStatements = BuildStatements(elseNode.Statements);
            Statements ensureStatements = null;
            if (node.EnsureClause is Pm.EnsureNode ensureNode) ensureStatements = BuildStatements(ensureNode.Statements);

            return new Body(statements, rescues, elseStatements, ensureStatements, span);
        }

        private static Statements/*!*/ Prepend(Statements/*!*/ prologue, Statements/*!*/ body) {
            var result = new Statements();
            // the parameter-binding prologue is not a line of the source (TracePoint :line)
            foreach (var statement in prologue) result.Add(statement, Statements.NoLine);
            for (int i = 0; i < body.Count; i++) result.Add(body[i], body.GetStartLine(i));
            if (body.Count == 0) {
                // an empty body is nil, not whatever the last parameter-binding statement
                // happened to evaluate to
                result.Add(Literal.Nil(SourceSpan.None));
            }
            return result;
        }

        private RescueClause/*!*/ Rescue(Pm.RescueNode/*!*/ node) {
            var types = new List<Expression>();
            foreach (var exception in node.Exceptions) types.Add(Argument(exception));
            return new RescueClause(types.ToArray(),
                node.Reference != null ? Target(node.Reference) : null,
                BuildStatements(node.Statements), Span(node));
        }

        private Body/*!*/ DefinitionBody(Pm.PmNode body, SourceSpan span, Statements prologue) {
            if (body is Pm.BeginNode begin) {
                return BuildBeginBody(begin, span, prologue);
            }
            var statements = BuildStatements(body);
            if (prologue != null) statements = Prepend(prologue, statements);
            return new Body(statements, null, null, null, span);
        }

        private Statements/*!*/ BlockBody(Pm.PmNode body, SourceSpan span, Statements prologue) {
            Statements statements;
            if (body is Pm.BeginNode begin) {
                statements = new Statements(BuildBeginBody(begin, span, null));
            } else {
                statements = BuildStatements(body);
            }
            return prologue != null ? Prepend(prologue, statements) : statements;
        }

        // ---- definitions ----

        private BlockDefinition/*!*/ BlockDef(Pm.BlockNode/*!*/ node) {
            var span = Span(node);
            var scope = new BlockLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                Statements prologue;
                var parameters = BlockParameters(node.Parameters, true, out prologue);
                return new BlockDefinition(scope, parameters, BlockBody(node.Body, span, prologue), span);
            } finally {
                _scopes.Pop();
            }
        }

        private Parameters BlockParameters(Pm.PmNode parametersNode, bool autoSplat, out Statements prologue) {
            prologue = null;
            switch (parametersNode) {
                case null:
                    return null;
                case Pm.BlockParametersNode blockParams: {
                    foreach (var blockLocal in blockParams.Locals) {
                        // `|y; x|` shadows: x is a fresh variable in the block, even when the
                        // enclosing scope already has one. Resolving would write through to it.
                        CurrentScope.AddVariable(((Pm.BlockLocalVariableNode)blockLocal).Name, Span(blockLocal));
                    }
                    return blockParams.Parameters != null
                        ? BuildParameters((Pm.ParametersNode)blockParams.Parameters, autoSplat, out prologue)
                        : null;
                }
                case Pm.NumberedParametersNode numbered: {
                    var span = Span(numbered);
                    var mandatory = new LeftValue[numbered.Maximum];
                    var declared = new RubyParameterSignature.Parameter[numbered.Maximum];
                    for (int i = 0; i < numbered.Maximum; i++) {
                        mandatory[i] = DefineParameter("_" + (i + 1), span);
                        declared[i] = new RubyParameterSignature.Parameter("req", "_" + (i + 1));
                    }
                    var numberedNames = new string[numbered.Maximum];
                    for (int i = 0; i < numbered.Maximum; i++) {
                        numberedNames[i] = "_" + (i + 1);
                    }
                    return new Parameters(mandatory, mandatory.Length, null, null, null, span) {
                        Signature = new RubyParameterSignature(declared, declared.Length, 0, 0, false, 0, false, false) {
                            ImplicitParameterNames = numberedNames
                        }
                    };
                }
                case Pm.ItParametersNode it: {
                    var span = Span(it);
                    var mandatory = new LeftValue[] { DefineParameter("it", span) };
                    // `it` is a parameter without a name as far as #parameters is concerned
                    var declared = new[] { new RubyParameterSignature.Parameter("req", null) };
                    return new Parameters(mandatory, 1, null, null, null, span) {
                        Signature = new RubyParameterSignature(declared, 1, 0, 0, false, 0, false, false) {
                            ImplicitParameterNames = new[] { "it" }
                        }
                    };
                }
                default:
                    throw Unsupported(parametersNode);
            }
        }

        private Expression/*!*/ Def(Pm.DefNode/*!*/ node, SourceSpan span) {
            Expression target = node.Receiver != null ? Expr(node.Receiver) : null;
            var scope = new MethodLexicalScope(CurrentScope);
            _scopes.Push(scope);
            bool enclosingUsesBlock = _usesBlock;
            _usesBlock = false;
            try {
                Statements prologue = null;
                Parameters parameters = Parameters.Empty;
                var enclosingZSuperArguments = _zsuperArguments;
                _zsuperArguments = null;
                try {
                    if (node.Parameters != null) {
                        var parametersNode = (Pm.ParametersNode)node.Parameters;
                        if (parametersNode.Block != null || parametersNode.KeywordRest is Pm.ForwardingParameterNode) {
                            _usesBlock = true;
                        }
                        parameters = BuildParameters(parametersNode, false, true, out prologue);
                    }
                    if (node.DeferredBodyLoader != null) {
                        // A body that holds nothing reported at parse time is mapped when first asked for.
                        // What mapping it now would have set _usesBlock to the skim has noted.
                        if ((node.BodyContents & PrismLoader.ContentsEager) == 0) {
                            var deferred = new DeferredMethodBody(this, node, scope, span, prologue, _zsuperArguments);
                            return new MethodDefinition(scope, target, node.Name, parameters, deferred.Materialize, span) {
                                UsesBlock = _usesBlock || (node.BodyContents & PrismLoader.ContentsUsesBlock) != 0
                            };
                        }
                        node.Body = node.DeferredBodyLoader.LoadDeferredBody(node);
                        node.DeferredBodyLoader = null;
                    }
                    var body = DefinitionBody(node.Body, span, prologue);
                    return new MethodDefinition(scope, target, node.Name, parameters, body, span) { UsesBlock = _usesBlock };
                } finally {
                    _zsuperArguments = enclosingZSuperArguments;
                }
            } finally {
                _scopes.Pop();
                _usesBlock = enclosingUsesBlock;
            }
        }

        /// <summary>
        /// A method body left in prism's buffer, mapped the first time the method's AST body is asked
        /// for - normally when the method is first called and compiled. It is mapped as Def would have
        /// mapped it: in the method's own scope, after its parameters, with the same super arguments.
        /// </summary>
        private sealed class DeferredMethodBody {
            private readonly PrismAstBridge/*!*/ _bridge;
            private readonly Pm.DefNode/*!*/ _node;
            private readonly LexicalScope/*!*/ _scope;
            private readonly SourceSpan _span;
            private readonly Statements _prologue;
            private readonly Arguments _zsuperArguments;

            public DeferredMethodBody(PrismAstBridge/*!*/ bridge, Pm.DefNode/*!*/ node, LexicalScope/*!*/ scope, SourceSpan span,
                Statements prologue, Arguments zsuperArguments) {
                _bridge = bridge;
                _node = node;
                _scope = scope;
                _span = span;
                _prologue = prologue;
                _zsuperArguments = zsuperArguments;
            }

            public Body/*!*/ Materialize() {
                bool timed = PhaseTimer.Enter(PhaseTimer.DefBridge);
                try {
                    return _bridge.MaterializeBody(_node, _scope, _span, _prologue, _zsuperArguments);
                } finally {
                    PhaseTimer.Exit(timed);
                }
            }
        }

        private Body/*!*/ MaterializeBody(Pm.DefNode/*!*/ node, LexicalScope/*!*/ scope, SourceSpan span, Statements prologue,
            Arguments zsuperArguments) {
            // the bridge keeps state while it maps; methods of one file may be first called on different threads
            lock (this) {
                Pm.PmNode bodyNode = node.DeferredBodyLoader.LoadDeferredBody(node);
                var enclosingUsesBlock = _usesBlock;
                var enclosingZSuperArguments = _zsuperArguments;
                _scopes.Push(scope);
                _usesBlock = false;
                _zsuperArguments = zsuperArguments;
                try {
                    var body = DefinitionBody(bodyNode, span, prologue);
                    // the skim has to have seen what mapping the body sees
                    System.Diagnostics.Debug.Assert(_usesBlock == ((node.BodyContents & PrismLoader.ContentsUsesBlock) != 0));
                    return body;
                } finally {
                    _scopes.Pop();
                    _usesBlock = enclosingUsesBlock;
                    _zsuperArguments = enclosingZSuperArguments;
                }
            }
        }

        /// <summary>
        /// MRI rejects a yield whose nearest enclosing method-or-class body is a class, module or
        /// singleton class body, or the top level. Blocks and lambdas are transparent.
        /// </summary>
        private bool IsYieldAllowed() {
            for (LexicalScope scope = CurrentScope; scope != null; scope = scope.OuterScope) {
                if (scope is MethodLexicalScope) {
                    return true;
                }
                if (scope is ClassLexicalScope || scope is TopLocalDefinitionLexicalScope) {
                    return false;
                }
                if (scope is TopStaticLexicalScope) {
                    return !_evalOutsideMethod;
                }
            }
            return true;
        }

        private Expression/*!*/ Class(Pm.ClassNode/*!*/ node, SourceSpan span) {
            var name = ConstantPath(node.ConstantPath, Span(node.ConstantPath));
            Expression superClass = node.Superclass != null ? Expr(node.Superclass) : null;
            var scope = new ClassLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                return new ClassDefinition(scope, name, superClass, DefinitionBody(node.Body, span, null), span);
            } finally {
                _scopes.Pop();
            }
        }

        private Expression/*!*/ Module(Pm.ModuleNode/*!*/ node, SourceSpan span) {
            var name = ConstantPath(node.ConstantPath, Span(node.ConstantPath));
            var scope = new TopLocalDefinitionLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                return new ModuleDefinition(scope, name, DefinitionBody(node.Body, span, null), span);
            } finally {
                _scopes.Pop();
            }
        }

        private int _indexTempCount;

        /// <summary>
        /// recv[i] op= value. An operator assignment reads through the left value and then
        /// writes through it, and both halves transform the index expressions again - so an
        /// index with a side effect ran twice, and `h[k] ||= v` called k a second time on
        /// its way to storing the default. Evaluating the indexes into temporaries first
        /// leaves one evaluation, which is what MRI does.
        /// </summary>
        private Expression/*!*/ IndexOperatorAssignment(Expression/*!*/ receiver, Pm.PmNode argumentsNode,
            Expression/*!*/ value, string operation, SourceSpan span) {

            Arguments arguments = argumentsNode is Pm.ArgumentsNode args
                ? BuildArguments(args) : new Arguments();
            var expressions = arguments.Expressions;

            var statements = new Statements();
            var hoisted = new Expression[expressions.Length];
            for (int i = 0; i < expressions.Length; i++) {
                // A splat is expanded once, into an array that both halves splat again (so #to_a
                // runs once); a literal needs no temporary.
                if (expressions[i] is SplattedArgument splat) {
                    // ResolveOrAddVariable: see the note in Hoist - a for-loop body's scope
                    // takes no definitions, so the temp has to land in the scope around it.
                    var array = CurrentScope.ResolveOrAddVariable("?index" + _indexTempCount++ + "?", span);
                    statements.Add(new SimpleAssignmentExpression(array,
                        new ArrayConstructor(new Arguments(new Expression[] { splat }), span), null, span));
                    hoisted[i] = new SplattedArgument(array);
                    continue;
                }
                if (expressions[i] is Literal) {
                    hoisted[i] = expressions[i];
                    continue;
                }
                var temp = CurrentScope.ResolveOrAddVariable("?index" + _indexTempCount++ + "?", span);
                statements.Add(new SimpleAssignmentExpression(temp, expressions[i], null, span));
                hoisted[i] = temp;
            }

            if (statements.Count == 0) {
                return new SimpleAssignmentExpression(
                    new ArrayItemAccess(receiver, arguments, null, span), value, operation, span);
            }

            statements.Add(new SimpleAssignmentExpression(
                new ArrayItemAccess(receiver, new Arguments(hoisted), null, span), value, operation, span));
            return new BlockExpression(statements, span);
        }

        // ---- parameters (including keyword-argument lowering) ----

        /// <summary>
        /// The argument list a parameterless `super` in the method being built should use, or
        /// null to let <see cref="SuperCall"/> read the parameters itself. It is only needed for
        /// a signature with keyword parameters: those were lowered onto a trailing hash, so the
        /// hash the parent expects has to be rebuilt from the keyword locals.
        /// </summary>
        private Arguments _zsuperArguments;

        /// <summary>
        /// Whether every positional parameter has a name <see cref="ZSuperPositionalArguments"/> can
        /// read it back by: no destructuring, no anonymous `*`.
        /// </summary>
        private static bool CanReadBackPositionals(Pm.ParametersNode/*!*/ node) {
            foreach (var required in node.Requireds) {
                if (!(required is Pm.RequiredParameterNode)) return false;
            }
            if (node.Rest != null && !(node.Rest is Pm.RestParameterNode rest && rest.Name != null)) return false;
            foreach (var post in node.Posts) {
                if (!(post is Pm.RequiredParameterNode)) return false;
            }
            return true;
        }

        /// <summary>
        /// The positional parameters of a generally-lowered signature, read back by name.
        /// Returns null if one of them has no name to read it back by, in which case the
        /// original argument list kept aside by the prologue is forwarded instead.
        /// </summary>
        private List<Expression> ZSuperPositionalArguments(Pm.ParametersNode/*!*/ node) {
            if (!CanReadBackPositionals(node)) return null;
            var result = new List<Expression>();

            foreach (var required in node.Requireds) {
                if (!(required is Pm.RequiredParameterNode param)) return null;
                result.Add(CurrentScope.ResolveOrAddVariable(param.Name, Span(required)));
            }

            foreach (var opt in node.Optionals) {
                var param = (Pm.OptionalParameterNode)opt;
                result.Add(CurrentScope.ResolveOrAddVariable(param.Name, Span(opt)));
            }

            if (node.Rest != null) {
                if (!(node.Rest is Pm.RestParameterNode rest) || rest.Name == null) return null;
                result.Add(new SplattedArgument(CurrentScope.ResolveOrAddVariable(rest.Name, Span(node.Rest))));
            }

            foreach (var post in node.Posts) {
                if (!(post is Pm.RequiredParameterNode param)) return null;
                result.Add(CurrentScope.ResolveOrAddVariable(param.Name, Span(post)));
            }

            return result;
        }

        /// <summary>
        ///   def m(a, b: 1, **rest); super; end   =>   super(a, **rest, b: b)
        ///   def m(a, b: 1); super; end           =>   super(a, b: b)
        ///   def m(a, **rest); super; end         =>   super(a, **rest)
        ///   def m(a, **nil); super; end          =>   super(a)
        /// The keyword values are read at the call site, so a body that reassigns one (or the
        /// rest) passes the new value, as MRI does. Forwarding the hidden trailing hash instead
        /// would pass the keywords as they arrived - and when none did, its `{}` as one more
        /// positional argument, where MRI's `**` passes nothing. The hash of the named keywords
        /// is the call's own and never empty, so it is passed as it is, with a rest merged into
        /// it first - which, as in MRI, has to be a Hash still. A lone `**rest` passes nothing
        /// for an empty hash (SuperCall makes that two plain call sites), and otherwise the hash
        /// the call passed while rest still holds just that, a copy of rest once it does not
        /// (KeywordArgumentSplat.PassingOn); a rest set to something else goes through #to_hash.
        /// None of these adds a local to the method, which every method would carry, super or
        /// not. Returns null when the positional parameters cannot be read back.
        /// </summary>
        private Arguments ZSuperArguments(Pm.ParametersNode/*!*/ node, List<Expression> positional) {
            if (positional == null) {
                return null;
            }

            var span = Span(node);
            var arguments = new List<Expression>(positional);

            Expression rest = null;
            if (node.KeywordRest is Pm.KeywordRestParameterNode keywordRest) {
                // an anonymous `**` is bound to a hidden local
                rest = CurrentScope.ResolveOrAddVariable(keywordRest.Name ?? AnonymousKeywordRestName, span);
            }

            if (node.Keywords.Length > 0) {
                var maplets = new List<Maplet>();
                foreach (var keyword in node.Keywords) {
                    string name = keyword is Pm.RequiredKeywordParameterNode required
                        ? required.Name
                        : ((Pm.OptionalKeywordParameterNode)keyword).Name;
                    maplets.Add(new Maplet(new SymbolLiteral(name, _encoding, span),
                        CurrentScope.ResolveOrAddVariable(name, span), span));
                }
                Expression hash = new HashConstructor(maplets.ToArray(), true, span);
                if (rest != null) {
                    // MRI orders the forwarded hash rest-first: `def m(b: 1, **rest)` called with
                    // z: 2 supers with {z: 2, b: 1}. It also wants rest to be a Hash still (TypeError
                    // otherwise, nil included), so there is no #to_hash fallback and no temporary for
                    // one: a temporary is one more local in every such method, super or not.
                    hash = KeywordSplatMerge.HashOnly(
                        KeywordSplatMerge.HashOnly(new HashConstructor(new Maplet[0], true, span), rest, span), hash, span);
                }
                arguments.Add(hash);
            } else if (rest != null) {
                // A lone **rest goes the way `**rest` does - nil passes nothing, anything else but a
                // Hash through #to_hash, as in MRI - and that fallback reads the local again rather
                // than a temporary.
                var converted = new MethodCall(new HashConstructor(new Maplet[0], true, span), "merge",
                    new Arguments(new OrExpression(rest, new HashConstructor(new Maplet[0], span), span)), span);
                // the hidden parameter the keywords arrived in (LowerKeywords, LowerGeneralParameters)
                var keywords = CurrentScope.ResolveVariable("?kw?");
                arguments.Add(keywords != null
                    ? KeywordArgumentSplat.PassingOn(rest, converted, keywords)
                    : new KeywordArgumentSplat(rest, converted));
            }
            return new Arguments(arguments.ToArray());
        }

        private Parameters/*!*/ BuildParameters(Pm.ParametersNode/*!*/ node, bool autoSplat, out Statements prologue) {
            return BuildParameters(node, autoSplat, false, out prologue);
        }

        private Parameters/*!*/ BuildParameters(Pm.ParametersNode/*!*/ node, bool autoSplat, bool isMethod, out Statements prologue) {
            var result = BuildParametersWorker(node, autoSplat, isMethod, out prologue);
            result.Signature = BuildSignature(node);
            return result;
        }

        /// <summary>
        /// The declared parameter list, which is what #parameters and #arity have to report.
        /// It is recorded separately because the lowering below rewrites keywords - and in the
        /// general case the whole positional list - into something else entirely.
        /// </summary>
        private static RubyParameterSignature/*!*/ BuildSignature(Pm.ParametersNode/*!*/ node) {
            var parameters = new List<RubyParameterSignature.Parameter>();

            foreach (var required in node.Requireds) {
                // a destructured parameter is still one required parameter, but it has no name
                parameters.Add(new RubyParameterSignature.Parameter("req",
                    (required as Pm.RequiredParameterNode)?.Name));
            }
            int leadingCount = parameters.Count;

            foreach (var opt in node.Optionals) {
                parameters.Add(new RubyParameterSignature.Parameter("opt", ((Pm.OptionalParameterNode)opt).Name));
            }

            // `|a,|` is an implicit rest: MRI leaves it out of #parameters and does not let it
            // make the arity variadic, so only an explicit `*` counts here
            bool hasRest = node.Rest is Pm.RestParameterNode;
            if (hasRest) {
                parameters.Add(new RubyParameterSignature.Parameter("rest",
                    ((Pm.RestParameterNode)node.Rest).Name ?? "*"));
            }

            foreach (var post in node.Posts) {
                parameters.Add(new RubyParameterSignature.Parameter("req",
                    (post as Pm.RequiredParameterNode)?.Name));
            }

            // MRI lists the required keywords first, whatever order they were declared in:
            // `def m(k: 1, j:)' has [[:keyreq, :j], [:key, :k]].
            int requiredKeywordCount = 0;
            foreach (var keyword in node.Keywords) {
                if (keyword is Pm.RequiredKeywordParameterNode required) {
                    parameters.Add(new RubyParameterSignature.Parameter("keyreq", required.Name));
                    requiredKeywordCount++;
                }
            }
            foreach (var keyword in node.Keywords) {
                if (keyword is Pm.OptionalKeywordParameterNode optional) {
                    parameters.Add(new RubyParameterSignature.Parameter("key", optional.Name));
                }
            }

            bool hasKeywordRest = false;
            if (node.KeywordRest is Pm.KeywordRestParameterNode keywordRest) {
                parameters.Add(new RubyParameterSignature.Parameter("keyrest", keywordRest.Name ?? "**"));
                hasKeywordRest = true;
            } else if (node.KeywordRest is Pm.NoKeywordsParameterNode) {
                parameters.Add(new RubyParameterSignature.Parameter("nokey", null));
            } else if (node.KeywordRest is Pm.ForwardingParameterNode) {
                // `def m(...)` forwards everything, and reports itself as `*, **, &`
                parameters.Add(new RubyParameterSignature.Parameter("rest", "*"));
                parameters.Add(new RubyParameterSignature.Parameter("keyrest", "**"));
                parameters.Add(new RubyParameterSignature.Parameter("block", "&"));
                hasRest = true;
                hasKeywordRest = true;
            }

            if (node.Block is Pm.BlockParameterNode block) {
                parameters.Add(new RubyParameterSignature.Parameter("block", block.Name ?? "&"));
            }

            return new RubyParameterSignature(parameters.ToArray(), leadingCount, node.Optionals.Length,
                node.Posts.Length, hasRest, requiredKeywordCount, node.Keywords.Length > 0, hasKeywordRest);
        }

        private Parameters/*!*/ BuildParametersWorker(Pm.ParametersNode/*!*/ node, bool autoSplat, bool isMethod, out Statements prologue) {
            var span = Span(node);
            prologue = null;

            // A destructured parameter is a CompoundLeftValue, which only the block calling
            // convention can bind directly. Methods get a plain hidden parameter plus a
            // parallel assignment in the prologue: `def m((a, b))` => `def m(?d0?); (a, b) = ?d0?`.
            List<Expression> destructuring = isMethod ? new List<Expression>() : null;

            var mandatory = new List<LeftValue>();
            foreach (var required in node.Requireds) {
                switch (required) {
                    case Pm.RequiredParameterNode requiredParam:
                        mandatory.Add(DefineParameter(requiredParam.Name, Span(required)));
                        break;
                    case Pm.MultiTargetNode multi:
                        mandatory.Add(DestructuredParameter(multi, destructuring, Span(required)));
                        break;
                    default:
                        throw Unsupported(required);
                }
            }
            int leadingMandatoryCount = mandatory.Count;

            var optional = new List<SimpleAssignmentExpression>();
            foreach (var opt in node.Optionals) {
                var optParam = (Pm.OptionalParameterNode)opt;
                var lhs = DefineParameter(optParam.Name, Span(opt));
                optional.Add(new SimpleAssignmentExpression(lhs, Expr(optParam.Value), null, Span(opt)));
            }

            LeftValue unsplat = null;
            if (node.Rest != null) {
                var restSpan = Span(node.Rest);
                if (node.Rest is Pm.ImplicitRestNode) {
                    // |a,| occurs only in blocks/lambdas (invalid in def); a placeholder
                    // keeps MRI arity semantics
                    unsplat = Placeholder.Singleton;
                } else if (node.Rest is Pm.RestParameterNode restParam) {
                    unsplat = DefineParameter(restParam.Name ?? Symbols.RestArgsLocal, restSpan);
                } else {
                    throw Unsupported(node.Rest);
                }
            }

            foreach (var post in node.Posts) {
                switch (post) {
                    case Pm.RequiredParameterNode postParam:
                        mandatory.Add(DefineParameter(postParam.Name, Span(post)));
                        break;
                    case Pm.MultiTargetNode multi:
                        mandatory.Add(DestructuredParameter(multi, destructuring, Span(post)));
                        break;
                    default:
                        throw Unsupported(post);
                }
            }

            bool keywordsAfterOptionals = false;

            // keyword arguments: with only mandatory positionals a trailing optional hash
            // is equivalent, so use that (it keeps arity and Method#parameters sane).
            // Anything richer needs positional binding done by hand, because keywords do
            // not have their own calling-convention slot here.
            if (node.Keywords.Length > 0 || node.KeywordRest is Pm.KeywordRestParameterNode) {
                // A method with optional positionals and nothing else past them keeps them as
                // optional parameters, the keywords lowered after them as below: the keyword hash
                // of a call that leaves an optional out is moved into place before the defaults run
                // (Parameters.KeywordsAfterOptionals). The general lowering puts everything in one
                // splat, which no call can bind without allocating, and the method JIT does not take.
                //
                // A block's keyword parameters must not count towards the auto-splat decision:
                // `{ |a, **k| }` takes one positional parameter, so a single Array argument is
                // passed whole. The general lowering is the one that knows that, since it binds
                // the positionals itself.
                if (isMethod && optional.Count > 0 && unsplat == null && node.Posts.Length == 0 && !autoSplat) {
                    int optionalPositionals = optional.Count;
                    // `super' passes the positionals by name: the mandatory ones are locals (a
                    // destructured one a hidden local), and so are the optional ones
                    var positionals = new List<Expression>(mandatory);
                    foreach (var opt in optional) {
                        positionals.Add((Expression)opt.Left);
                    }
                    prologue = LowerKeywords(node, optional, true, mandatory, isMethod, span, optionalPositionals);
                    _zsuperArguments = ZSuperArguments(node, positionals);
                    keywordsAfterOptionals = true;
                } else if (optional.Count > 0 || unsplat != null || node.Posts.Length > 0 || autoSplat) {
                    return LowerGeneralParameters(node, autoSplat, isMethod, span, out prologue);
                } else {
                    // a lambda checks arity as strictly as a method does; a plain block does not
                    prologue = LowerKeywords(node, optional, isMethod || !autoSplat, mandatory, isMethod, span, 0);
                    if (isMethod) {
                        // only leading mandatory parameters can be here, and they are all locals
                        _zsuperArguments = ZSuperArguments(node, new List<Expression>(mandatory));
                    }
                }
            } else if (node.KeywordRest is Pm.ForwardingParameterNode) {
                // def f(...) => def f(*?fwd?, &?fwdblk?); calls with `...` splat them back
                if (unsplat != null) throw Unsupported(node.KeywordRest);
                unsplat = DefineParameter(ForwardingRestName, Span(node.KeywordRest));
            } else if (node.KeywordRest is Pm.NoKeywordsParameterNode) {
                // `def m(**nil)` accepts no keywords at all, which is only detectable by
                // looking at the trailing hash - so it takes one, just to refuse it
                // (a lambda is as strict as a method about this; a plain block is not)
                if ((isMethod || !autoSplat) && optional.Count == 0 && unsplat == null) {
                    prologue = RefuseKeywords(optional, mandatory, span);
                    if (isMethod) {
                        // `super` must not forward the hash taken only to be refused
                        _zsuperArguments = ZSuperArguments(node, new List<Expression>(mandatory));
                    }
                }
            } else if (node.KeywordRest != null) {
                throw Unsupported(node.KeywordRest);
            }

            LocalVariable blockParam = null;
            if (node.Block is Pm.BlockParameterNode block) {
                blockParam = DefineParameter(block.Name ?? "?block?", Span(node.Block));
            } else if (node.KeywordRest is Pm.ForwardingParameterNode) {
                blockParam = DefineParameter(ForwardingBlockName, Span(node.KeywordRest));
            }

            if (destructuring != null && destructuring.Count > 0) {
                var statements = new Statements();
                foreach (var assignment in destructuring) statements.Add(assignment);
                if (prologue != null) {
                    foreach (var statement in prologue) statements.Add(statement);
                }
                prologue = statements;
            }

            return new Parameters(mandatory.ToArray(), leadingMandatoryCount,
                optional.Count > 0 ? optional.ToArray() : null, unsplat, blockParam, span) {
                KeywordsAfterOptionals = keywordsAfterOptionals
            };
        }

        /// <summary>
        /// A destructured parameter `(a, b)`. In a block it is bound by the calling convention
        /// itself, so the CompoundLeftValue goes straight into the parameter list. A method has
        /// no such convention, so it gets a hidden parameter plus a parallel assignment that
        /// <paramref name="destructuring"/> collects for the body prologue.
        /// </summary>
        private LeftValue/*!*/ DestructuredParameter(Pm.MultiTargetNode/*!*/ node, List<Expression> destructuring, SourceSpan span) {
            var compound = CompoundTarget(node.Lefts, node.Rest, node.Rights);
            if (destructuring == null) {
                return compound;
            }

            var parameter = CurrentScope.AddVariable("?destructure" + _tempCounter++ + "?", span);
            destructuring.Add(new DestructuringAssignmentExpression(compound, parameter, span));
            return parameter;
        }

        /// <summary>
        /// Signatures mixing keywords with optional/rest/post positionals are lowered to a
        /// single splat whose contents the prologue binds by hand:
        ///
        ///   def m(a, b = 5, *c, d, e: 2, **k)
        ///     =&gt; def m(*?args?)
        ///        ?args? = ?args?[0] if <auto-splat, blocks only>
        ///        ?kw?   = Hash === ?args?.last ? ?args?.pop : {}
        ///        a = ?args?.shift
        ///        b = ?args?.size &gt; 1 ? ?args?.shift : 5     # 1 = number of posts
        ///        c = ?args?.shift(?args?.size - 1)
        ///        d = ?args?.shift
        ///        e, k from ?kw?
        ///
        /// Execution semantics match MRI; arity and Method#parameters do not.
        /// </summary>
        private Parameters/*!*/ LowerGeneralParameters(Pm.ParametersNode/*!*/ node, bool autoSplat, bool isMethod, SourceSpan span, out Statements prologue) {
            var args = DefineParameter("?args?", span);
            var statements = new Statements();
            int postCount = node.Posts.Length;

            var kwVar = CurrentScope.AddVariable("?kw?", span);

            // Everything below consumes ?args? destructively, so a parameterless `super`
            // inside the body would find nothing left to forward. Keep the list as it
            // arrived - trailing keyword hash included - and let SuperCall splat that.
            // Only a method needs it: a `super` in a block forwards the arguments of the
            // method the block is in (SuperCall reads gen.CurrentMethod.Parameters).
            // A signature whose positionals can all be read back by name has `super` rebuilt from
            // them instead (ZSuperArguments, below), and does not pay for this copy on every call.
            LocalVariable superArgs = null;
            if (isMethod && !CanReadBackPositionals(node)) {
                superArgs = CurrentScope.AddVariable("?superargs?", span);
                statements.Add(new SimpleAssignmentExpression(superArgs,
                    SplatParameterRead.Copy(args, span), null, span));
            }

            // A trailing Hash is how a keyword call arrives here, since keywords have no
            // calling-convention slot of their own. But when a block auto-splats a single
            // Array argument no keywords were passed, so the Hash stays positional
            // (Ruby 3 keyword separation).
            var popKeywords = new SimpleAssignmentExpression(kwVar,
                SplatParameterRead.PopKeywords(args, span), null, span);

            int formalCount = node.Requireds.Length + node.Optionals.Length + postCount + (node.Rest != null ? 1 : 0);
            if (autoSplat && formalCount > 1) {
                // the Array is copied: taking it apart must not empty the caller's array
                statements.Add(new IfExpression(
                    SplatParameterRead.IsAutoSplat(args, span),
                    MakeStatements(
                        new SimpleAssignmentExpression(args,
                            SplatParameterRead.AutoSplat(args, span), null, span),
                        new SimpleAssignmentExpression(kwVar, new HashConstructor(new Maplet[0], span), null, span)),
                    new List<ElseIfClause> { new ElseIfClause(null, MakeStatements(popKeywords), span) },
                    span));
            } else {
                statements.Add(popKeywords);
            }

            // The lowered signature is a single splat, so nothing checks how many
            // positional arguments actually arrived - a method or lambda has to do it
            // itself, after the keyword hash was taken off the end. Without this a Hash
            // passed positionally to `def m(a, b = nil, **o)` looked like the keywords
            // it is not and was silently dropped.
            if (isMethod || !autoSplat) {
                int minimum = node.Requireds.Length + postCount;
                int maximum = node.Rest != null ? -1 : minimum + node.Optionals.Length;
                if (minimum > 0 || maximum >= 0) {
                    statements.Add(SplatParameterRead.CheckArity(args, minimum, maximum, RequiredKeywordNames(node), span));
                }
            }

            foreach (var required in node.Requireds) {
                statements.Add(AssignNext(required, args, span));
            }

            foreach (var opt in node.Optionals) {
                var optParam = (Pm.OptionalParameterNode)opt;
                var local = CurrentScope.ResolveOrAddVariable(optParam.Name, Span(opt));
                statements.Add(new SimpleAssignmentExpression(local,
                    SplatParameterRead.ShiftOptional(args, postCount, Expr(optParam.Value), span),
                    null, span));
            }

            if (node.Rest is Pm.RestParameterNode rest && (rest.Name != null || isMethod)) {
                var local = CurrentScope.ResolveOrAddVariable(rest.Name ?? Symbols.RestArgsLocal, Span(node.Rest));
                statements.Add(new SimpleAssignmentExpression(local,
                    SplatParameterRead.ShiftRest(args, postCount, span),
                    null, span));
            } else if (node.Rest != null && postCount > 0) {
                // anonymous rest still has to consume what the posts do not take
                statements.Add(SplatParameterRead.ShiftRest(args, postCount, span));
            }

            foreach (var post in node.Posts) {
                statements.Add(AssignNext(post, args, span));
            }

            foreach (var statement in BindKeywordsFrom(node, kwVar, isMethod)) {
                statements.Add(statement);
            }

            prologue = statements;

            if (isMethod) {
                _zsuperArguments = ZSuperArguments(node, ZSuperPositionalArguments(node));
                System.Diagnostics.Debug.Assert((_zsuperArguments == null) == (superArgs != null));
            }

            LocalVariable blockParam = null;
            if (node.Block is Pm.BlockParameterNode block) {
                blockParam = DefineParameter(block.Name ?? "?block?", Span(node.Block));
            }
            return new Parameters(Array.Empty<LeftValue>(), 0, null, args, blockParam, span) {
                SuperSplat = superArgs
            };
        }

        /// <summary>
        /// Emits `target = ?args?.shift`. A destructured parameter such as |(a, b)| or
        /// |(*)| resolves to a CompoundLeftValue, which only a parallel assignment can
        /// take; handing one to SimpleAssignmentExpression trips its Debug.Assert.
        /// </summary>
        private Expression/*!*/ AssignNext(Pm.PmNode/*!*/ target, LocalVariable/*!*/ args, SourceSpan span) {
            var shift = SplatParameterRead.Shift(args, span);
            var lhs = Target(target);
            return lhs is CompoundLeftValue compound
                ? (Expression)new DestructuringAssignmentExpression(compound, shift, span)
                : new SimpleAssignmentExpression(lhs, shift, null, span);
        }

        /// <summary>
        /// def m(a, k: 1, j:, **rest) becomes def m(a, ?kw? = {}) plus a prologue:
        ///   raise ArgumentError, "missing keyword: :j" unless ?kw?.key?(:j)
        ///   j = ?kw?[:j]
        ///   k = ?kw?.key?(:k) ? ?kw?[:k] : 1
        ///   rest = ?kw?.dup ; rest.delete(:j) ; rest.delete(:k)
        /// </summary>
        private Statements/*!*/ LowerKeywords(Pm.ParametersNode/*!*/ node, List<SimpleAssignmentExpression>/*!*/ optional,
            bool strict, List<LeftValue>/*!*/ mandatory, bool isMethod, SourceSpan span, int optionalPositionals) {

            var kwVar = CurrentScope.AddVariable("?kw?", span);
            // The default stands for "the caller passed no keywords", so it is marked as keyword
            // arguments itself - that is what tells it apart from a Hash passed positionally - and
            // as a hash this call alone holds, which **rest takes over rather than copies. A method
            // without **rest only ever reads it (the checks and bindings below; `super' passes the
            // keywords on by name), so it gets the runtime's shared empty one instead of a new hash
            // per call that omits its keywords.
            bool readOnly = isMethod && !(node.KeywordRest is Pm.KeywordRestParameterNode);
            optional.Add(new SimpleAssignmentExpression(kwVar,
                readOnly ? HashConstructor.SharedOmittedKeywords(span) : HashConstructor.OmittedKeywords(span), null, span));

            var prologue = new Statements();
            // blocks and lambdas pass nil for parameters the caller omitted, so the
            // optional-parameter default above does not fire for them. A method gets the
            // default whenever the caller omitted it, so a nil there is one the caller passed
            // positionally - `m(nil)' - which the separation check below has to refuse.
            if (!isMethod) {
                prologue.Add(new SimpleAssignmentExpression(kwVar,
                    HashConstructor.OmittedKeywords(span), "||", span));
            }

            if (strict) {
                foreach (var check in KeywordSeparationChecks(kwVar, mandatory, optionalPositionals, true, RequiredKeywordNames(node), span)) {
                    prologue.Add(check);
                }
            }

            foreach (var statement in BindKeywordsFrom(node, kwVar, isMethod)) {
                prologue.Add(statement);
            }
            return prologue;
        }

        /// <summary>
        /// Ruby 3 does not convert a positional Hash into keyword arguments, and the hash a
        /// keyword call really did produce is not an extra positional argument either. Both
        /// mistakes surface as an arity error, which the lowered signature (keywords became a
        /// trailing optional parameter) would otherwise not report:
        ///
        ///   def m(a, b: 1)
        ///   m(1, {"x" =&gt; 1})    # the hash landed in ?kw?: given 2, expected 1
        ///   m("a" =&gt; 1, b: 2)   # the keywords landed in a:  given 0, expected 1
        /// </summary>
        private List<Expression>/*!*/ KeywordSeparationChecks(LocalVariable/*!*/ kwVar, List<LeftValue>/*!*/ mandatory,
            int optionalPositionals, bool acceptsKeywords, string[]/*!*/ requiredKeywords, SourceSpan span) {

            var checks = new List<Expression>();
            int count = mandatory.Count;
            string expected = (optionalPositionals > 0) ? count + ".." + (count + optionalPositionals) : count.ToString();

            var lastMandatory = count > 0 ? mandatory[count - 1] as LocalVariable : null;
            if (lastMandatory != null) {
                checks.Add(new IfExpression(
                    new KeywordArgumentsTest(lastMandatory, span),
                    new Statements(acceptsKeywords
                        ? RaiseArityError(count - 1, expected, requiredKeywords, span)
                        : RaiseError("ArgumentError", "no keywords accepted", span)),
                    new List<ElseIfClause>(), span));
            }

            checks.Add(new UnlessExpression(
                new KeywordArgumentsTest(kwVar, span),
                new Statements(RaiseArityError(count + optionalPositionals + 1, expected, requiredKeywords, span)), null, span));

            if (!acceptsKeywords) {
                checks.Add(new UnlessExpression(
                    new MethodCall(kwVar, "empty?", null, span),
                    new Statements(RaiseError("ArgumentError", "no keywords accepted", span)), null, span));
            }

            return checks;
        }

        /// <summary>
        /// The prologue of `def m(**nil)`: it takes the trailing hash like any keyword-accepting
        /// method would, only to reject it.
        /// </summary>
        private Statements/*!*/ RefuseKeywords(List<SimpleAssignmentExpression>/*!*/ optional, List<LeftValue>/*!*/ mandatory, SourceSpan span) {
            var kwVar = CurrentScope.AddVariable("?kw?", span);
            optional.Add(new SimpleAssignmentExpression(kwVar, new HashConstructor(new Maplet[0], true, span), null, span));

            var prologue = new Statements();
            foreach (var check in KeywordSeparationChecks(kwVar, mandatory, 0, false, new string[0], span)) {
                prologue.Add(check);
            }
            return prologue;
        }

        /// <summary>
        /// MRI's arity error lists the required keywords, given or not:
        /// "wrong number of arguments (given 2, expected 1; required keyword: k)".
        /// </summary>
        private static string[]/*!*/ RequiredKeywordNames(Pm.ParametersNode node) {
            var names = new List<string>();
            if (node != null) {
                foreach (var keyword in node.Keywords) {
                    if (keyword is Pm.RequiredKeywordParameterNode required) {
                        names.Add(required.Name);
                    }
                }
            }
            return names.ToArray();
        }

        private Expression/*!*/ RaiseArityError(int given, string/*!*/ expected, string[]/*!*/ requiredKeywords, SourceSpan span) {
            return RaiseError("ArgumentError",
                "wrong number of arguments (given " + given + ", expected " + expected +
                RubyParameterSignature.DescribeRequiredKeywords(requiredKeywords) + ")", span);
        }

        /// <summary>
        /// Binds each declared keyword parameter (and **rest) out of the hash the caller
        /// passed, raising ArgumentError for missing required keywords like MRI does.
        /// </summary>
        private List<Expression>/*!*/ BindKeywordsFrom(Pm.ParametersNode/*!*/ node, LocalVariable/*!*/ kwVar, bool isMethod) {
            var span = Span(node);
            var prologue = new List<Expression>();
            var names = new List<string>();
            var requiredNames = new List<string>();
            var optionalNames = new List<string>();

            foreach (var keyword in node.Keywords) {
                var kwSpan = Span(keyword);
                switch (keyword) {
                    case Pm.RequiredKeywordParameterNode required: {
                        names.Add(required.Name);
                        requiredNames.Add(required.Name);
                        var local = CurrentScope.ResolveOrAddVariable(required.Name, kwSpan);
                        prologue.Add(new SimpleAssignmentExpression(local,
                            KeywordParameterRead.Value(kwVar, required.Name, names.Count - 1, null, _encoding, kwSpan),
                            null, kwSpan));
                        break;
                    }
                    case Pm.OptionalKeywordParameterNode optKeyword: {
                        names.Add(optKeyword.Name);
                        optionalNames.Add(optKeyword.Name);
                        var local = CurrentScope.ResolveOrAddVariable(optKeyword.Name, kwSpan);
                        prologue.Add(new SimpleAssignmentExpression(local,
                            KeywordParameterRead.Value(kwVar, optKeyword.Name, names.Count - 1, Expr(optKeyword.Value), _encoding, kwSpan),
                            null, kwSpan));
                        break;
                    }
                    default:
                        throw Unsupported(keyword);
                }
            }

            if (node.KeywordRest is Pm.KeywordRestParameterNode keywordRest && (keywordRest.Name != null || isMethod)) {
                var kwSpan = Span(node.KeywordRest);
                var restLocal = CurrentScope.ResolveOrAddVariable(keywordRest.Name ?? AnonymousKeywordRestName, kwSpan);
                prologue.Add(new SimpleAssignmentExpression(restLocal,
                    KeywordParameterRead.Rest(kwVar, names.ToArray(), _encoding, kwSpan), null, kwSpan));
            }

            // MRI reports every missing keyword in one error, and does so before complaining
            // about keywords it does not know; a signature with **rest knows them all.
            //
            // Both errors are spelled with an array difference, which allocates three arrays and
            // builds a set to say "nothing is wrong" - on every call. So they sit behind one
            // runtime call that allocates nothing and is true exactly when there is nothing to
            // report; the array difference runs only to word the error.
            var checks = new Statements();
            if (requiredNames.Count > 0) {
                // Every required keyword is there, or something is missing and the list says what.
                Expression allPresent = null;
                foreach (var name in requiredNames) {
                    var present = new MethodCall(kwVar, "key?",
                        new Arguments(new SymbolLiteral(name, _encoding, span)), span);
                    allPresent = (allPresent == null) ? (Expression)present : new AndExpression(allPresent, present, span);
                }
                checks.Add(new UnlessExpression(allPresent, new Statements(RaiseForKeywordList(
                    new MethodCall(SymbolArray(requiredNames, span), "-",
                        new Arguments(new MethodCall(kwVar, "keys", null, span)), span),
                    "missing keyword: ", "missing keywords: ", span)), null, span));
            }
            if (!(node.KeywordRest is Pm.KeywordRestParameterNode)) {
                // As many keys as there are declared keywords among them means there is nothing
                // else in the hash, since a key the signature declares is one it knows. Counting
                // them costs a #key? per keyword and allocates nothing.
                Expression declaredPresent = Literal.Integer(0, span);
                foreach (var name in names) {
                    declaredPresent = new MethodCall(declaredPresent, "+", new Arguments(
                        new ConditionalExpression(
                            new MethodCall(kwVar, "key?",
                                new Arguments(new SymbolLiteral(name, _encoding, span)), span),
                            Literal.Integer(1, span), Literal.Integer(0, span), span)), span);
                }
                checks.Add(new UnlessExpression(
                    new MethodCall(new MethodCall(kwVar, "size", null, span), "==",
                        new Arguments(declaredPresent), span),
                    new Statements(RaiseForKeywordList(
                        new MethodCall(new MethodCall(kwVar, "keys", null, span), "-",
                            new Arguments(SymbolArray(names, span)), span),
                        "unknown keyword: ", "unknown keywords: ", span)), null, span));
            }

            var result = new List<Expression>();
            if (checks.Count > 0) {
                var ordered = new List<string>(requiredNames);
                ordered.AddRange(optionalNames);
                result.Add(new UnlessExpression(
                    KeywordParameterRead.Match(kwVar, ordered.ToArray(), requiredNames.Count,
                        node.KeywordRest is Pm.KeywordRestParameterNode, _encoding, span),
                    checks, null, span));
            }
            result.AddRange(prologue);
            return result;
        }

        private Expression/*!*/ SymbolArray(List<string>/*!*/ names, SourceSpan span) {
            var items = new List<Expression>();
            foreach (var name in names) items.Add(new SymbolLiteral(name, _encoding, span));
            return new ArrayConstructor(new Arguments(items.ToArray()), span);
        }

        /// <summary>
        /// `raise ArgumentError, "&lt;singular|plural&gt;#{list}" unless list.empty?`, where the list
        /// is spelled the way MRI spells it: ":a, :b, :c" - Array#inspect without the brackets.
        /// </summary>
        private Expression/*!*/ RaiseForKeywordList(Expression/*!*/ list, string/*!*/ singular, string/*!*/ plural, SourceSpan span) {
            var listVar = CurrentScope.AddVariable("?kwlist" + _tempCounter++ + "?", span);
            var message = new MethodCall(
                new ConditionalExpression(
                    new MethodCall(new MethodCall(listVar, "size", null, span), "==",
                        new Arguments(Literal.Integer(1, span)), span),
                    new StringLiteral(singular, _encoding, span),
                    new StringLiteral(plural, _encoding, span), span),
                "+",
                new Arguments(new MethodCall(new MethodCall(listVar, "inspect", null, span), "[]",
                    new Arguments(new RangeExpression(Literal.Integer(1, span), Literal.Integer(-2, span), false, span)), span)),
                span);

            return new BlockExpression(MakeStatements(
                new SimpleAssignmentExpression(listVar, list, null, span),
                new UnlessExpression(
                    new MethodCall(listVar, "empty?", null, span),
                    new Statements(new MethodCall(KernelModule(span), "raise", new Arguments(new Expression[] {
                        new ConstantVariable(null, "ArgumentError", span), message
                    }), span)),
                    null, span)), span);
        }


        // ---- pattern matching (case/in), lowered to tests + bindings ----

        private const string ForwardingRestName = "?fwd?";
        internal const string AnonymousKeywordRestName = "?kwrest?";
        private const string ForwardingBlockName = "?fwdblk?";

        private static Statements/*!*/ MakeStatements(params Expression[]/*!*/ statements) {
            var result = new Statements();
            foreach (var statement in statements) result.Add(statement);
            return result;
        }

        private LocalVariable/*!*/ NewTemp(Expression/*!*/ value, SourceSpan span, out Expression/*!*/ assignment) {
            var temp = CurrentScope.ResolveOrAddVariable("?pm" + _tempCounter++ + "?", span);
            assignment = new SimpleAssignmentExpression(temp, value, null, span);
            return temp;
        }

        /// <summary>
        /// `::Kernel`, the receiver of the raises this lowering emits. An implicit-self `raise` would run a
        /// `raise` the surrounding class defines itself (Fiber#raise, Thread#raise, a user's own), and a bare
        /// `ArgumentError` a constant of that name in the lexical scope.
        /// </summary>
        private static Expression/*!*/ KernelModule(SourceSpan span) {
            return new ConstantVariable(null, "Kernel", span);
        }

        private Expression/*!*/ RaiseError(string/*!*/ className, string/*!*/ message, SourceSpan span) {
            return new MethodCall(KernelModule(span), "raise", new Arguments(new Expression[] {
                new ConstantVariable(null, className, span),
                new StringLiteral(message, _encoding, span)
            }), span);
        }

        /// <summary>
        /// `subject.deconstruct`, reusing the value already computed for the enclosing `case/in`
        /// if there is one. MRI calls #deconstruct once per case statement, not once per clause.
        /// </summary>
        private Expression/*!*/ DeconstructCall(Expression/*!*/ subject, SourceSpan span) {
            var call = new MethodCall(subject, "deconstruct", null, span);

            LocalVariable[] cache;
            var local = subject as LocalVariable;
            if (local == null || !_deconstructCache.TryGetValue(local, out cache)) {
                return call;
            }
            return new ConditionalExpression(cache[1], cache[0],
                new BlockExpression(MakeStatements(
                    new SimpleAssignmentExpression(cache[1], Literal.True(span), null, span),
                    new SimpleAssignmentExpression(cache[0], call, null, span)), span),
                span);
        }

        /// <summary>
        /// Appends the tests that turn <paramref name="subject"/> into the Array an array/find
        /// pattern matches against, and returns the temp holding it. Unlike a plain
        /// `Array === subject` shortcut this always goes through #deconstruct, which objects
        /// (including Arrays with a singleton #deconstruct) are allowed to override.
        /// </summary>
        private LocalVariable/*!*/ DeconstructToArray(Expression/*!*/ subject, List<Expression>/*!*/ tests, SourceSpan span) {
            tests.Add(Explained(new MethodCall(subject, "respond_to?",
                new Arguments(new SymbolLiteral("deconstruct", _encoding, span)), span),
                "deconstruct", new Expression[] { subject }, span));

            Expression arrAssign;
            var arr = NewTemp(DeconstructCall(subject, span), span, out arrAssign);
            tests.Add(new BlockExpression(MakeStatements(arrAssign, Literal.True(span)), span));
            tests.Add(new OrExpression(
                CaseEqual(new ConstantVariable("Array", span), arr, span),
                RaiseError("TypeError", "deconstruct must return Array", span), span));
            return arr;
        }

        private Expression/*!*/ RaiseNoMatchingPattern(Expression/*!*/ subject, SourceSpan span) {
            Expression message = _patternFailure == null
                ? new MethodCall(subject, "inspect", null, span)
                : new MethodCall(new ConstantVariable(null, "NoMatchingPatternError", span), "__message__",
                    new Arguments(new Expression[] { subject, _patternFailure }), span);
            Expression result = new MethodCall(KernelModule(span), "raise", new Arguments(new Expression[] {
                new ConstantVariable(null, "NoMatchingPatternError", span),
                message
            }), span);

            if (_patternMissingKey != null) {
                result = new ConditionalExpression(
                    new MethodCall(_patternMissingKey, "nil?", null, span),
                    result,
                    new MethodCall(KernelModule(span), "raise", new Arguments(
                        new MethodCall(new ConstantVariable(null, "NoMatchingPatternKeyError", span), "__missing_key__",
                            new Arguments(new Expression[] { subject, _patternMissingKey }), span)
                    ), span),
                    span);
            }
            return result;
        }

        /// <summary>
        /// Each attempt at a match - an `in` clause, a branch of `|` - starts with no key missing,
        /// so that only the failure that ends the match decides the exception.
        /// </summary>
        private Expression/*!*/ ResetMissingKey(Expression/*!*/ test, SourceSpan span) {
            if (_patternMissingKey == null && _patternFailure == null) {
                return test;
            }
            var statements = new List<Expression>();
            if (_patternMissingKey != null) {
                statements.Add(new SimpleAssignmentExpression(_patternMissingKey, Literal.Nil(span), null, span));
            }
            if (_patternFailure != null) {
                statements.Add(new SimpleAssignmentExpression(_patternFailure, Literal.Nil(span), null, span));
            }
            statements.Add(test);
            return new BlockExpression(MakeStatements(statements.ToArray()), span);
        }

        /// <summary>
        /// <paramref name="test"/>, which on failing records why in <see cref="_patternFailure"/>
        /// (when there is one): kind names the reason and args are what the message quotes, as
        /// NoMatchingPatternError.__reason__ in the prelude formats them.
        /// </summary>
        private Expression/*!*/ Explained(Expression/*!*/ test, string/*!*/ kind, Expression[]/*!*/ args, SourceSpan span) {
            if (_patternFailure == null) {
                return test;
            }
            var reasonArgs = new List<Expression> { new SymbolLiteral(kind, _encoding, span) };
            reasonArgs.AddRange(args);
            return new OrExpression(test, new BlockExpression(MakeStatements(new Expression[] {
                new SimpleAssignmentExpression(_patternFailure,
                    new MethodCall(new ConstantVariable(null, "NoMatchingPatternError", span), "__reason__",
                        new Arguments(reasonArgs.ToArray()), span), null, span),
                Literal.False(span)
            }), span), span);
        }

        /// <summary>pattern === subject, evaluating the pattern once so the reason can quote it.</summary>
        private Expression/*!*/ ValuePattern(Expression/*!*/ pattern, Expression/*!*/ subject, SourceSpan span) {
            if (_patternFailure == null) {
                return CaseEqual(pattern, subject, span);
            }
            Expression assign;
            var temp = NewTemp(pattern, span, out assign);
            return new BlockExpression(MakeStatements(new Expression[] {
                assign,
                Explained(CaseEqual(temp, subject, span), "eqq", new Expression[] { temp, subject }, span)
            }), span);
        }

        private Expression/*!*/ CaseMatch(Pm.CaseMatchNode/*!*/ node, SourceSpan span) {
            Expression assign;
            var temp = NewTemp(Expr(node.Predicate), span, out assign);

            // #deconstruct is called at most once per `case`, however many `in` clauses look at it
            var cachedValue = CurrentScope.ResolveOrAddVariable("?pmd" + _tempCounter++ + "?", span);
            var cachedFlag = CurrentScope.ResolveOrAddVariable("?pmd" + _tempCounter++ + "?", span);
            // reset on every entry: the temps outlive one iteration of an enclosing loop
            var resetCache = new SimpleAssignmentExpression(cachedFlag, Literal.False(span), null, span);
            _deconstructCache[temp] = new[] { cachedValue, cachedFlag };

            var clauses = new List<ElseIfClause>();
            Expression firstTest = null;
            Statements firstBody = null;
            var enclosingMissingKey = _patternMissingKey;
            var enclosingFailure = _patternFailure;
            bool raises = !(node.ElseClause is Pm.ElseNode);
            _patternMissingKey = raises ? CurrentScope.ResolveOrAddVariable("?pmk" + _tempCounter++ + "?", span) : null;
            _patternFailure = raises ? CurrentScope.ResolveOrAddVariable("?pmf" + _tempCounter++ + "?", span) : null;
            foreach (var condition in node.Conditions) {
                var inNode = (Pm.InNode)condition;
                var test = ResetMissingKey(PatternTest(inNode.Pattern, temp, Span(inNode)), Span(inNode));
                var body = BuildStatements(inNode.Statements);
                if (firstTest == null) {
                    firstTest = test;
                    firstBody = body;
                } else {
                    clauses.Add(new ElseIfClause(test, body, Span(inNode)));
                }
            }
            if (node.ElseClause is Pm.ElseNode elseNode) {
                clauses.Add(new ElseIfClause(null, BuildStatements(elseNode.Statements), Span(elseNode)));
            } else {
                clauses.Add(new ElseIfClause(null, new Statements(RaiseNoMatchingPattern(temp, span)), span));
            }
            _patternMissingKey = enclosingMissingKey;
            _patternFailure = enclosingFailure;

            _deconstructCache.Remove(temp);
            var ifExpr = new IfExpression(firstTest, firstBody, clauses, span);
            return new BlockExpression(MakeStatements(new Expression[] { assign, resetCache, ifExpr }), span);
        }

        /// <summary>
        /// Builds an expression that is truthy iff <paramref name="pattern"/> matches
        /// <paramref name="subject"/> (a temp local), binding capture variables as a
        /// side effect. Mirrors MRI semantics: === for value patterns,
        /// deconstruct/deconstruct_keys for array/hash patterns.
        /// </summary>
        private Expression/*!*/ PatternTest(Pm.PmNode/*!*/ pattern, Expression/*!*/ subject, SourceSpan span) {
            // Nested patterns recurse through here rather than through Expr (see StackGuard).
            if (!StackGuard.IsSufficient) {
                return StackGuard.RunOnFreshStack(() => PatternTest(pattern, subject, span));
            }
            switch (pattern) {
                case Pm.LocalVariableTargetNode target: {
                    var local = CurrentScope.ResolveOrAddVariable(target.Name, Span(target));
                    return BindTrue(local, subject, Span(target));
                }
                case Pm.CapturePatternNode capture: {
                    var target = (Pm.LocalVariableTargetNode)capture.Target;
                    var local = CurrentScope.ResolveOrAddVariable(target.Name, Span(target));
                    return new AndExpression(PatternTest(capture.Value, subject, span),
                        BindTrue(local, subject, Span(capture)), span);
                }
                case Pm.AlternationPatternNode alternation:
                    return new OrExpression(ResetMissingKey(PatternTest(alternation.Left, subject, span), span),
                        ResetMissingKey(PatternTest(alternation.Right, subject, span), span), span);
                case Pm.PinnedVariableNode pinned:
                    return ValuePattern(Expr(pinned.Variable), subject, Span(pinned));
                case Pm.PinnedExpressionNode pinnedExpr:
                    return ValuePattern(Expr(pinnedExpr.Expression), subject, Span(pinnedExpr));
                case Pm.IfNode guard: {
                    // `in pat if cond`: prism nests the pattern in the guard's statements
                    var inner = ((Pm.StatementsNode)guard.Statements).Body[0];
                    return new AndExpression(PatternTest(inner, subject, span),
                        Explained(Condition(guard.Predicate), "guard", new Expression[0], span), Span(guard));
                }
                case Pm.UnlessNode guard: {
                    var inner = ((Pm.StatementsNode)guard.Statements).Body[0];
                    return new AndExpression(PatternTest(inner, subject, span),
                        Explained(new NotExpression(Expr(guard.Predicate), Span(guard)), "guard", new Expression[0], span), Span(guard));
                }
                case Pm.ArrayPatternNode arrayPattern:
                    return ArrayPattern(arrayPattern, subject, span);
                case Pm.HashPatternNode hashPattern:
                    return HashPattern(hashPattern, subject, span);
                case Pm.FindPatternNode findPattern:
                    return FindPattern(findPattern, subject, span);
                default:
                    return ValuePattern(Expr(pattern), subject, span); // value pattern
            }
        }

        private Expression/*!*/ BindTrue(LocalVariable/*!*/ local, Expression/*!*/ value, SourceSpan span) {
            return new BlockExpression(MakeStatements(new Expression[] {
                new SimpleAssignmentExpression(local, value, null, span),
                Literal.True(span)
            }), span);
        }

        private Expression/*!*/ CaseEqual(Expression/*!*/ pattern, Expression/*!*/ subject, SourceSpan span) {
            return new MethodCall(pattern, "===", new Arguments(subject), span);
        }

        private static Expression/*!*/ AndAll(List<Expression>/*!*/ tests, SourceSpan span) {
            Expression result = null;
            foreach (var test in tests) {
                result = result == null ? test : new AndExpression(result, test, span);
            }
            return result;
        }

        private Expression/*!*/ ArrayPattern(Pm.ArrayPatternNode/*!*/ node, Expression/*!*/ subject, SourceSpan span) {
            var tests = new List<Expression>();
            if (node.Constant != null) {
                tests.Add(ValuePattern(Expr(node.Constant), subject, span));
            }

            var arr = DeconstructToArray(subject, tests, span);

            int required = node.Requireds.Length + node.Posts.Length;
            Expression lenAssign;
            var len = NewTemp(new MethodCall(arr, "length", null, span), span, out lenAssign);
            tests.Add(new BlockExpression(MakeStatements(new Expression[] { lenAssign, Literal.True(span) }), span));
            tests.Add(Explained(new MethodCall(len, node.Rest != null ? ">=" : "==",
                new Arguments(Literal.Integer(required, span)), span),
                "length", new Expression[] { arr, len, Literal.Integer(required, span), node.Rest != null ? Literal.True(span) : Literal.False(span) }, span));

            for (int i = 0; i < node.Requireds.Length; i++) {
                tests.Add(ElementPattern(node.Requireds[i], arr, Literal.Integer(i, span), span));
            }

            if (node.Rest is Pm.SplatNode splat && splat.Expression != null) {
                var restLocal = (LocalVariable)Target(splat.Expression);
                // rest = arr[requireds, len - required]
                var restValue = new MethodCall(arr, "[]", new Arguments(new Expression[] {
                    Literal.Integer(node.Requireds.Length, span),
                    new MethodCall(len, "-", new Arguments(Literal.Integer(required, span)), span)
                }), span);
                tests.Add(BindTrue(restLocal, restValue, span));
            }

            for (int i = 0; i < node.Posts.Length; i++) {
                // index = len - postCount + i
                var index = new MethodCall(len, "-", new Arguments(Literal.Integer(node.Posts.Length - i, span)), span);
                tests.Add(ElementPattern(node.Posts[i], arr, index, span));
            }

            return AndAll(tests, span);
        }

        private Expression/*!*/ ElementPattern(Pm.PmNode/*!*/ pattern, LocalVariable/*!*/ arr, Expression/*!*/ index, SourceSpan span) {
            Expression elemAssign;
            var elem = NewTemp(new MethodCall(arr, "[]", new Arguments(index), span), span, out elemAssign);
            return new BlockExpression(MakeStatements(new Expression[] {
                elemAssign,
                PatternTest(pattern, elem, span)
            }), span);
        }

        private Expression/*!*/ HashPattern(Pm.HashPatternNode/*!*/ node, Expression/*!*/ subject, SourceSpan span) {
            var tests = new List<Expression>();
            if (node.Constant != null) {
                tests.Add(ValuePattern(Expr(node.Constant), subject, span));
            }

            var knownKeys = new List<string>();
            foreach (var element in node.Elements) {
                knownKeys.Add(LiteralText(((Pm.SymbolNode)((Pm.AssocNode)element).Key).Unescaped));
            }

            // MRI hands #deconstruct_keys the keys the pattern is interested in, or nil when the
            // pattern needs every key: a **rest or **nil looks at the keys it does not name, and
            // `in {}` demands that there be none. A bare ** needs nothing beyond the named keys.
            bool hasRest = node.Rest != null && !(node.Rest is Pm.AssocSplatNode anonymous && anonymous.Value == null);
            bool hasKeys = node.Elements.Length > 0 || node.Rest != null;
            Expression keyList;
            if (!hasKeys || hasRest) {
                keyList = Literal.Nil(span);
            } else {
                var symbols = new List<Expression>();
                foreach (var key in knownKeys) symbols.Add(new SymbolLiteral(key, _encoding, span));
                keyList = new ArrayConstructor(new Arguments(symbols.ToArray()), span);
            }

            tests.Add(Explained(new MethodCall(subject, "respond_to?",
                new Arguments(new SymbolLiteral("deconstruct_keys", _encoding, span)), span),
                "deconstruct_keys", new Expression[] { subject }, span));

            Expression hashAssign;
            var hash = NewTemp(new MethodCall(subject, "deconstruct_keys", new Arguments(keyList), span),
                span, out hashAssign);
            tests.Add(new BlockExpression(MakeStatements(hashAssign, Literal.True(span)), span));
            tests.Add(new OrExpression(
                CaseEqual(new ConstantVariable("Hash", span), hash, span),
                RaiseError("TypeError", "deconstruct_keys must return Hash", span), span));

            // `in {}` is the one hash pattern that demands an empty hash
            if (node.Elements.Length == 0 && node.Rest == null) {
                tests.Add(Explained(new MethodCall(hash, "empty?", null, span), "empty", new Expression[] { hash }, span));
            }

            foreach (var element in node.Elements) {
                var assoc = (Pm.AssocNode)element;
                var key = (Pm.SymbolNode)assoc.Key;
                var keySymbol = new SymbolLiteral(LiteralText(key.Unescaped), _encoding, Span(key));
                Expression hasKey = new MethodCall(hash, "key?", new Arguments(keySymbol), span);
                if (_patternMissingKey != null) {
                    hasKey = new OrExpression(hasKey, new BlockExpression(MakeStatements(new Expression[] {
                        new SimpleAssignmentExpression(_patternMissingKey,
                            new SymbolLiteral(LiteralText(key.Unescaped), _encoding, Span(key)), null, span),
                        Literal.False(span)
                    }), span), span);
                }
                tests.Add(hasKey);

                Pm.PmNode valuePattern = assoc.Value is Pm.ImplicitNode implicitValue ? implicitValue.Value : assoc.Value;
                Expression valueAssign;
                var value = NewTemp(new MethodCall(hash, "[]", new Arguments(new SymbolLiteral(LiteralText(key.Unescaped), _encoding, span)), span),
                    span, out valueAssign);
                tests.Add(new BlockExpression(MakeStatements(new Expression[] {
                    valueAssign,
                    PatternTest(valuePattern, value, span)
                }), span));
            }

            if (node.Rest is Pm.AssocSplatNode restSplat && restSplat.Value != null) {
                var restLocal = (LocalVariable)Target(restSplat.Value);
                var restStatements = new List<Expression> {
                    new SimpleAssignmentExpression(restLocal, new MethodCall(hash, "dup", null, span), null, span)
                };
                foreach (var key in knownKeys) {
                    restStatements.Add(new MethodCall(restLocal, "delete",
                        new Arguments(new SymbolLiteral(key, _encoding, span)), span));
                }
                restStatements.Add(Literal.True(span));
                tests.Add(new BlockExpression(MakeStatements(restStatements.ToArray()), span));
            } else if (node.Rest is Pm.NoKeywordsParameterNode) {
                // {**nil}: no keys beyond the listed ones — (h.keys - [...]).empty?
                var listed = new List<Expression>();
                foreach (var key in knownKeys) listed.Add(new SymbolLiteral(key, _encoding, span));
                var listedAgain = new List<Expression>();
                foreach (var key in knownKeys) listedAgain.Add(new SymbolLiteral(key, _encoding, span));
                tests.Add(Explained(new MethodCall(
                    new MethodCall(new MethodCall(hash, "keys", null, span), "-",
                        new Arguments(new ArrayConstructor(new Arguments(listed.ToArray()), span)), span),
                    "empty?", null, span),
                    "rest", new Expression[] { hash, new ArrayConstructor(new Arguments(listedAgain.ToArray()), span) }, span));
            }

            return AndAll(tests, span);
        }

        /// <summary>
        /// [*pre, a, b, *post]: scans for the first index where the middle patterns
        /// match via the __pm_find_index__ prelude helper, then binds the splats.
        /// </summary>
        private Expression/*!*/ FindPattern(Pm.FindPatternNode/*!*/ node, Expression/*!*/ subject, SourceSpan span) {
            var tests = new List<Expression>();
            if (node.Constant != null) {
                tests.Add(ValuePattern(Expr(node.Constant), subject, span));
            }

            var arr = DeconstructToArray(subject, tests, span);

            int count = node.Requireds.Length;
            Expression lenAssign;
            var len = NewTemp(new MethodCall(arr, "length", null, span), span, out lenAssign);
            tests.Add(new BlockExpression(MakeStatements(lenAssign, Literal.True(span)), span));
            tests.Add(new MethodCall(len, ">=", new Arguments(Literal.Integer(count, span)), span));

            // captures inside the block must live in the enclosing scope
            PreDeclareCaptures(node.Requireds);

            var blockScope = new BlockLexicalScope(CurrentScope);
            _scopes.Push(blockScope);
            BlockDefinition probe;
            try {
                var index = CurrentScope.AddVariable("?pmi" + _tempCounter++ + "?", span);
                var elementTests = new List<Expression>();
                for (int i = 0; i < count; i++) {
                    var position = new MethodCall(index, "+", new Arguments(Literal.Integer(i, span)), span);
                    elementTests.Add(ElementPattern(node.Requireds[i], arr, position, span));
                }
                probe = new BlockDefinition(blockScope,
                    new Parameters(new LeftValue[] { index }, 1, null, null, null, span),
                    MakeStatements(AndAll(elementTests, span)), span);
            } finally {
                _scopes.Pop();
            }

            Expression idxAssign;
            var idx = NewTemp(new MethodCall(null, "__pm_find_index__",
                new Arguments(new Expression[] { arr, Literal.Integer(count, span) }), probe, span), span, out idxAssign);
            tests.Add(Explained(new BlockExpression(MakeStatements(idxAssign, idx), span), "find", new Expression[] { arr }, span));

            if (node.Left is Pm.SplatNode leftSplat && leftSplat.Expression != null) {
                var pre = (LocalVariable)Target(leftSplat.Expression);
                tests.Add(BindTrue(pre, new MethodCall(arr, "[]", new Arguments(new Expression[] {
                    Literal.Integer(0, span), idx }), span), span));
            }
            if (node.Right is Pm.SplatNode rightSplat && rightSplat.Expression != null) {
                var post = (LocalVariable)Target(rightSplat.Expression);
                var start = new MethodCall(idx, "+", new Arguments(Literal.Integer(count, span)), span);
                var length = new MethodCall(new MethodCall(len, "-", new Arguments(idx), span), "-",
                    new Arguments(Literal.Integer(count, span)), span);
                tests.Add(BindTrue(post, new MethodCall(arr, "[]",
                    new Arguments(new Expression[] { start, length }), span), span));
            }

            return AndAll(tests, span);
        }

        private void PreDeclareCaptures(Pm.PmNode[]/*!*/ patterns) {
            foreach (var pattern in patterns) PreDeclareCaptures(pattern);
        }

        private void PreDeclareCaptures(Pm.PmNode pattern) {
            switch (pattern) {
                case null: return;
                case Pm.LocalVariableTargetNode target:
                    CurrentScope.ResolveOrAddVariable(target.Name, Span(target));
                    return;
                case Pm.CapturePatternNode capture:
                    PreDeclareCaptures(capture.Value);
                    PreDeclareCaptures(capture.Target);
                    return;
                case Pm.ArrayPatternNode array:
                    PreDeclareCaptures(array.Requireds);
                    PreDeclareCaptures(array.Rest);
                    PreDeclareCaptures(array.Posts);
                    return;
                case Pm.SplatNode splat:
                    PreDeclareCaptures(splat.Expression);
                    return;
                case Pm.HashPatternNode hash:
                    foreach (var element in hash.Elements) {
                        if (element is Pm.AssocNode assoc) {
                            PreDeclareCaptures(assoc.Value is Pm.ImplicitNode imp ? imp.Value : assoc.Value);
                        }
                    }
                    return;
                default:
                    return;
            }
        }

        /// <summary>
        /// Defines a parameter in the current scope. Unlike ResolveOrAddVariable this never
        /// reaches into an enclosing scope, which would make one LocalVariable a formal
        /// parameter of two different scopes (mirrors Parser.DefineParameter).
        /// </summary>
        private LocalVariable/*!*/ DefineParameter(string/*!*/ name, SourceSpan span) {
            LocalVariable existing;
            if (CurrentScope.TryGetValue(name, out existing)) {
                // Duplicate parameter names are legal for `_`. Each occurrence still needs
                // its own slot (one LocalVariable cannot hold two closure indices), so give
                // the repeats hidden names; reads of `_` see the first one, as in MRI.
                return CurrentScope.AddVariable("?dup" + _tempCounter++ + "?", span);
            }
            return CurrentScope.AddVariable(name, span);
        }

        private static LeftValue/*!*/[]/*!*/ RemoveAt(LeftValue/*!*/[]/*!*/ values, int index) {
            var result = new LeftValue[values.Length - 1];
            Array.Copy(values, 0, result, 0, index);
            Array.Copy(values, index + 1, result, index, values.Length - index - 1);
            return result;
        }
    }
}

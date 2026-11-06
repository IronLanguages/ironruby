/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * Keyword parameters.
 *
 * PrismAstBridge.LowerKeywords turns `def m(a, k:, j: 1)' into `def m(a, ?kw? = {})' plus a
 * prologue that binds the keyword locals out of that hash:
 *
 *   raise ArgumentError, ... if KeywordArgumentsTest(a)             # separation checks
 *   raise ArgumentError, ... unless KeywordArgumentsTest(?kw?)
 *   unless KeywordArgumentsMatch(?kw?, [:k, :j], 1, false)          # missing / unknown
 *     ...word the ArgumentError the slow way...
 *   end
 *   k = GetKeywordArgument(?kw?, :k, 0)
 *   j = (v = GetKeywordArgument(?kw?, :j, 1)) == DefaultArgument ? 1 : v
 *   <the method's own statements>
 *
 * This class recognizes exactly that shape. A recognized method is specialized with the
 * keyword locals as typed parameters of its own, after the positional ones, and the entry
 * stub reads them out of the hash - the very helpers the prologue uses - after checking that
 * the prologue would have bound them without raising. Any call the prologue would complain
 * about (a missing or unknown keyword, a Hash passed positionally, keywords landing in the
 * last positional) fails that check and runs the generic body, which raises the error MRI
 * raises. A default that is a nil/true/false/Integer/Float literal or a Symbol is a constant
 * the entry fills in. Any other default (`b: a + 1', `list: []') is left to the typed body: the
 * entry hands it RubyOps.DefaultArgument for an omitted one, and the body evaluates the default
 * itself, in declaration order, before its own statements (JitCompiler.EmitKeywordDefaults) - unless
 * the default assigns a local, which could be a keyword bound after it. **rest is not recognized:
 * such a method is never wrapped.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;

namespace IronRuby.Runtime.Jit {
    using RExpr = IronRuby.Compiler.Ast.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    internal sealed class JitKeywords {
        internal const int MaxKeywords = 6;

        // How many leading statements of the body are the prologue; the typed body starts after them.
        internal int PrologueLength;

        // KeywordArgumentsMatch's arguments: required names first, then the optional ones.
        internal RubySymbol/*!*/[]/*!*/ MatchNames;
        internal int RequiredCount;

        // Per keyword, in declaration order: its local, its symbol, its position, its default.
        internal string/*!*/[]/*!*/ LocalNames;
        internal RubySymbol/*!*/[]/*!*/ Symbols;
        internal int[]/*!*/ Positions;
        internal object[]/*!*/ Defaults;
        internal bool[]/*!*/ Required;
        // Per keyword: the default the typed body evaluates (its Defaults entry is then
        // RubyOps.DefaultArgument), or null. Null when no keyword has one.
        internal RExpr[] DefaultExprs;

        /// <summary>A keyword whose default the typed body evaluates: its typed parameter is the boxed value or RubyOps.DefaultArgument.</summary>
        internal bool HasDefaultExpr(int index) {
            return DefaultExprs != null && DefaultExprs[index] != null;
        }

        internal int Count { get { return LocalNames.Length; } }

        // The optional positional parameters before the keyword hash (Parameters.KeywordsAfterOptionals):
        // `def m(a, b = 1, k: 2)' has one. Each has a literal default (OptionalDefaults); a call that
        // leaves one out may have put its keyword hash there, which the entry moves into place first.
        internal int OptionalPositionals;
        internal object[] OptionalDefaults;

        /// <summary>
        /// The keyword signature of a method whose parameters are mandatory positionals plus
        /// keywords lowered by LowerKeywords, or null if the method is anything else.
        /// </summary>
        internal static JitKeywords TryRecognize(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context) {
            var ps = ast.Parameters;
            if (ps.Unsplat != null || ps.Block != null) { return null; }
            if (ps.Optional.Length != 1 && !(ps.KeywordsAfterOptionals && ps.Optional.Length > 1)) { return null; }
            int optionalPositionals = ps.Optional.Length - 1;
            object[] optionalDefaults = null;
            if (optionalPositionals > 0) {
                // only defaults the entry can fill in up front: nil, true, false, a Fixnum or a Float
                optionalDefaults = new object[optionalPositionals];
                for (int k = 0; k < optionalPositionals; k++) {
                    var opt = ps.Optional[k];
                    var literal = opt.Right as Literal;
                    object v = (literal != null) ? literal.Value : null;
                    if (!(opt.Left is LocalVariable) || literal == null || !(v == null || v is bool || v is int || v is double)) {
                        return null;
                    }
                    optionalDefaults[k] = v;
                }
            }
            var kwVar = ps.Optional[optionalPositionals].Left as LocalVariable;
            if (kwVar == null || kwVar.Name != "?kw?") { return null; }

            var body = ast.Body;
            if (body == null || body.Statements == null) { return null; }
            var statements = body.Statements;
            int n = statements.Count;
            int i = 0;

            // ?kw? ||= {} (a block's prologue starts with it; a method's does not)
            if (i >= n) { return null; }
            var reset = statements[i] as SimpleAssignmentExpression;
            if (reset != null && reset.Operation == "||" && IsVar(reset.Left, kwVar) && reset.Right is HashConstructor) {
                i++;
            }

            // The separation checks: an if/unless on KeywordArgumentsTest whose body only raises.
            while (i < n) {
                var ifExpr = statements[i] as IfExpression;
                if (ifExpr != null && ifExpr.Condition is KeywordArgumentsTest &&
                    (ifExpr.ElseIfClauses == null || ifExpr.ElseIfClauses.Count == 0)) {
                    i++;
                    continue;
                }
                var unlessExpr = statements[i] as UnlessExpression;
                if (unlessExpr != null && unlessExpr.ElseClause == null) {
                    var test = unlessExpr.Condition as KeywordArgumentsTest;
                    if (test != null && IsVar(test.Value, kwVar)) {
                        i++;
                        continue;
                    }
                }
                break;
            }

            // unless KeywordArgumentsMatch(?kw?, names, required, false) ... end
            if (i >= n) { return null; }
            var matchStmt = statements[i] as UnlessExpression;
            if (matchStmt == null || matchStmt.ElseClause != null) { return null; }
            var match = matchStmt.Condition as KeywordParameterRead;
            if (match == null || !match.IsMatch || match.AcceptsRest || !IsVar(match.Keywords, kwVar)) { return null; }
            i++;

            var names = match.Names;
            if (names.Length == 0 || names.Length > MaxKeywords) { return null; }

            var result = new JitKeywords();
            result.OptionalPositionals = optionalPositionals;
            result.OptionalDefaults = optionalDefaults;
            result.RequiredCount = match.RequiredCountOrPosition;
            result.MatchNames = new RubySymbol[names.Length];
            for (int j = 0; j < names.Length; j++) {
                result.MatchNames[j] = context.CreateSymbol(names[j], match.Encoding);
            }

            // k = <read>, one per declared keyword, in declaration order
            var locals = new List<string>();
            var symbols = new List<RubySymbol>();
            var positions = new List<int>();
            var defaults = new List<object>();
            var required = new List<bool>();
            var defaultExprs = new List<RExpr>();
            bool anyDefaultExpr = false;
            while (i < n) {
                var bind = statements[i] as SimpleAssignmentExpression;
                if (bind == null || bind.Operation != null) { break; }
                var read = bind.Right as KeywordParameterRead;
                if (read == null || !read.IsValue || !IsVar(read.Keywords, kwVar)) { break; }
                var local = bind.Left as LocalVariable;
                if (local == null || local.DefinitionLexicalDepth != ast.DefinedScope.Depth) { return null; }
                if (read.RequiredCountOrPosition != locals.Count) { return null; }

                object defaultValue = null;
                RExpr defaultExpr = null;
                if (read.DefaultValue != null) {
                    // A literal is the same value every time and evaluating it does nothing.
                    var literal = read.DefaultValue as Literal;
                    var symbol = read.DefaultValue as SymbolLiteral;
                    object v = (literal != null) ? literal.Value : null;
                    if (literal != null && (v == null || v is int || v is double || v is bool)) {
                        defaultValue = v;
                    } else if (symbol != null && symbol.Value is string) {
                        defaultValue = context.CreateSymbol((string)symbol.Value, symbol.Encoding);
                    } else {
                        defaultValue = RubyOps.DefaultArgument;
                        defaultExpr = read.DefaultValue;
                        anyDefaultExpr = true;
                    }
                }

                locals.Add(local.Name);
                symbols.Add(context.CreateSymbol(read.Names[0], read.Encoding));
                positions.Add(read.RequiredCountOrPosition);
                defaults.Add(defaultValue);
                required.Add(read.DefaultValue == null);
                defaultExprs.Add(defaultExpr);
                i++;
            }
            if (locals.Count != names.Length) { return null; }

            // every name Match checks is one a binding reads, and vice versa
            for (int j = 0; j < names.Length; j++) {
                if (!symbols.Contains(result.MatchNames[j])) { return null; }
            }

            result.PrologueLength = i;
            result.LocalNames = locals.ToArray();
            result.Symbols = symbols.ToArray();
            result.Positions = positions.ToArray();
            result.Defaults = defaults.ToArray();
            result.Required = required.ToArray();
            result.DefaultExprs = anyDefaultExpr ? defaultExprs.ToArray() : null;
            return result;
        }

        /// <summary>
        /// What the generic body does first (Parameters.TransformOptionalsInitialization): a call that
        /// passed no keyword hash where it belongs but left an optional positional out may have put it
        /// in the last optional positional it did pass. <paramref name="args"/> holds the delegate's
        /// positional parameters and then the keyword hash, at <paramref name="keywordIndex"/>.
        /// </summary>
        internal static void ShiftKeywords(object[]/*!*/ args, int firstOptional, int keywordIndex) {
            if (args[keywordIndex] != RubyOps.DefaultArgument) { return; }
            for (int i = keywordIndex - 1; i >= firstOptional; i--) {
                if (args[i] != RubyOps.DefaultArgument) {
                    if (RubyOps.IsKeywordArgumentsHash(args[i])) {
                        args[keywordIndex] = args[i];
                        args[i] = RubyOps.DefaultArgument;
                    }
                    return;
                }
            }
        }

        private static bool IsVar(RExpr node, LocalVariable/*!*/ kwVar) {
            var lv = node as LocalVariable;
            return lv != null && lv.Name == kwVar.Name && lv.DefinitionLexicalDepth == kwVar.DefinitionLexicalDepth;
        }

        /// <summary>
        /// True when the prologue would bind every keyword from <paramref name="kw"/> without
        /// raising: it is the hash a keyword call made (or the call passed no keywords at all),
        /// it has every required keyword and no other.
        /// </summary>
        internal bool Matches(object kw) {
            if (kw == RubyOps.DefaultArgument) { return RequiredCount == 0; }
            var hash = kw as Hash;
            return hash != null && hash.IsKeywordArguments && hash.HasKeywords(MatchNames, RequiredCount, false);
        }

        /// <summary>What the prologue binds keyword <paramref name="index"/> to, given <see cref="Matches"/>.</summary>
        internal object Value(object kw, int index) {
            object v = RubyOps.GetKeywordArgument(kw, Symbols[index], Positions[index]);
            return (v == RubyOps.DefaultArgument) ? Defaults[index] : v;
        }

        // ---- Matches and Value in one go: what a specialized entry calls ----------------------
        //
        // A keyword call's hash is small, and Hash.TryGetValueByIdentity scans a small hash for the
        // Symbol rather than probing for it (8 entries, Hash.IdentityScanLimit). Looking each declared
        // keyword up once both binds it and counts the keys that are declared ones, so the hash has
        // no key the method does not know exactly when that count is all the keys it has - which is
        // what Matches answers with a scan of its own. Anything else takes the two steps above.

        private const int ScanLimit = 8;

        private bool Bind(Hash/*!*/ hash, int index, ref int found, out object value) {
            if (hash.TryGetValueByIdentity(Symbols[index], Positions[index], out value)) {
                found++;
                return true;
            }
            value = Defaults[index];
            return !Required[index];
        }

        private static Hash SmallKeywordHash(object kw) {
            var hash = kw as Hash;
            return (hash != null && hash.IsKeywordArguments && hash.Count <= ScanLimit) ? hash : null;
        }

        internal bool Read(object kw, out object v0) {
            var hash = SmallKeywordHash(kw);
            if (hash != null) {
                int found = 0;
                return Bind(hash, 0, ref found, out v0) && found == hash.Count;
            }
            bool ok = Matches(kw);
            v0 = ok ? Value(kw, 0) : null;
            return ok;
        }

        internal bool Read(object kw, out object v0, out object v1) {
            var hash = SmallKeywordHash(kw);
            if (hash != null) {
                int found = 0;
                v1 = null;
                return Bind(hash, 0, ref found, out v0) && Bind(hash, 1, ref found, out v1) && found == hash.Count;
            }
            bool ok = Matches(kw);
            v0 = ok ? Value(kw, 0) : null;
            v1 = ok ? Value(kw, 1) : null;
            return ok;
        }

        internal bool Read(object kw, out object v0, out object v1, out object v2) {
            var hash = SmallKeywordHash(kw);
            if (hash != null) {
                int found = 0;
                v1 = v2 = null;
                return Bind(hash, 0, ref found, out v0) && Bind(hash, 1, ref found, out v1) && Bind(hash, 2, ref found, out v2) &&
                    found == hash.Count;
            }
            bool ok = Matches(kw);
            v0 = ok ? Value(kw, 0) : null;
            v1 = ok ? Value(kw, 1) : null;
            v2 = ok ? Value(kw, 2) : null;
            return ok;
        }
    }
}

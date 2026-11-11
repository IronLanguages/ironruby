using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;

namespace IronRuby.Aot {
    /// <summary>
    /// Compile-time access to IronRuby's front end: a RubyContext to transform against, and the
    /// two transformations the runtime does (file -> top-level lambda, `def' AST -> body lambda).
    /// </summary>
    internal static class AotHost {
        public static RubyContext CreateContext(out ScriptEngine engine) {
            RubyContext.AlternativeParser = IronRuby.Prism.PrismAstBridge.Parse;
            var setup = new ScriptRuntimeSetup();
            var ruby = IronRuby.Ruby.CreateRubySetup();
            // SavePath marks the AstGenerator as "saving to disk": it turns off OSR outlining and
            // coverage, both of which bake runtime objects into the tree.
            ruby.Options["SavePath"] = Path.GetTempPath();
            // -X:JIT bakes inline caches holding the runtime into calls (JitKeywordCall); the
            // saved code goes without them
            ruby.Options["JIT"] = false;
            setup.LanguageSetups.Add(ruby);
            var runtime = IronRuby.Ruby.CreateRuntime(setup);
            engine = IronRuby.Ruby.GetEngine(runtime);
            return (RubyContext)HostingHelpers.GetLanguageContext(engine);
        }

        public static Expression<Func<RubyScope, object, object>> ParseFile(RubyContext context, string path) {
            var unit = context.CreateFileUnit(Path.GetFullPath(path));
            var options = new RubyCompilerOptions() { FactoryKind = TopScopeFactoryKind.File };
            var lambda = context.ParseSourceCode<Func<RubyScope, object, object>>(unit, options, ErrorSink.Default);
            if (lambda == null) throw new InvalidOperationException("syntax error in " + path);
            return lambda;
        }

        private static readonly FieldInfo _encodingField = typeof(RubyMethodBody).GetField("_encoding", BindingFlags.NonPublic | BindingFlags.Instance);

        public static RubyEncoding GetEncoding(RubyMethodBody body) => (RubyEncoding)_encodingField.GetValue(body);

        /// <summary>
        /// The census transformation of a method body: declaring scope and module are unknown at
        /// compile time, so they are passed as null constants.
        /// </summary>
        public static LambdaExpression TransformMethodBody(RubyContext context, RubyMethodBody body) {
            var gen = new AstGenerator(context, new RubyCompilerOptions(), body.Document, GetEncoding(body), false);
            return body.Ast.TransformBody(gen, null, null, null);
        }
    }
}

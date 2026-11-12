using System;
using System.Linq.Expressions;
using System.Reflection.Emit;

namespace IronRuby.Aot.Compiler {
    /// <summary>
    /// LambdaExpression.CompileToMethod for .NET Core: compiles a lambda into the body of a static
    /// MethodBuilder (e.g. one of a PersistedAssemblyBuilder's), setting its signature. Nested
    /// lambdas become private static methods of the same type. Constants must be IL literals
    /// (primitives, strings, null, Type, MethodBase); anything else throws, as it did on .NET
    /// Framework. DynamicExpressions must be rewritten away first.
    /// </summary>
    public static class ExpressionCompiler {
        public static void CompileToMethod(LambdaExpression lambda, MethodBuilder method) {
            if (!method.IsStatic) throw new ArgumentException("method must be static", nameof(method));
            LambdaCompiler.Compile(lambda, method, null);
        }
    }
}

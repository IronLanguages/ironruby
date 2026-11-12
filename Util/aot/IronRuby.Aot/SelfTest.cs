// Smoke test of the ported compiler on its own: closures, nested lambdas, try/catch/finally,
// loops, switch - compiled into a PersistedAssemblyBuilder, saved, loaded back and run.
using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using IronRuby.Aot.Compiler;

namespace IronRuby.Aot {
    internal static class SelfTest {
        public static int Run(string outDir) {
            Directory.CreateDirectory(outDir);
            var ab = new PersistedAssemblyBuilder(new AssemblyName("SelfTest"), typeof(object).Assembly);
            var mod = ab.DefineDynamicModule("SelfTest");
            var tb = mod.DefineType("T", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

            // int f(int n) { int acc = 0; Func<int,int> add = x => acc += x; for (i = 0; i < n; i++) add(i); try { throw } catch { acc += 1000 } finally {..}; return acc; }
            var n = Expression.Parameter(typeof(int), "n");
            var acc = Expression.Variable(typeof(int), "acc");
            var i = Expression.Variable(typeof(int), "i");
            var x = Expression.Parameter(typeof(int), "x");
            var add = Expression.Variable(typeof(Func<int, int>), "add");
            var brk = Expression.Label("brk");
            var e = Expression.Variable(typeof(Exception), "e");
            var body = Expression.Block(new[] { acc, i, add },
                Expression.Assign(add, Expression.Lambda<Func<int, int>>(Expression.AddAssign(acc, x), "adder", new[] { x })),
                Expression.Loop(
                    Expression.IfThenElse(Expression.LessThan(i, n),
                        Expression.Block(Expression.Invoke(add, i), Expression.PostIncrementAssign(i)),
                        Expression.Break(brk)), brk),
                Expression.TryCatchFinally(
                    Expression.Throw(Expression.New(typeof(InvalidOperationException)), typeof(int)),
                    Expression.Call(typeof(Console).GetMethod("WriteLine", new[] { typeof(string) }), Expression.Constant("finally ran")),
                    Expression.Catch(e, Expression.AddAssign(acc, Expression.Constant(1000)))),
                Expression.Switch(n, Expression.Empty(),
                    Expression.SwitchCase(Expression.AddAssign(acc, Expression.Constant(1)).ToVoid(), Expression.Constant(10))),
                acc);
            var lambda = Expression.Lambda<Func<int, int>>(body, "f", new[] { n });
            var mb = tb.DefineMethod("f", MethodAttributes.Public | MethodAttributes.Static);
            ExpressionCompiler.CompileToMethod(lambda, mb);
            tb.CreateType();
            string path = Path.Combine(outDir, "SelfTest.dll");
            ab.Save(path);

            var loaded = Assembly.LoadFile(Path.GetFullPath(path));
            var f = loaded.GetType("T").GetMethod("f");
            object result = f.Invoke(null, new object[] { 10 });
            Console.WriteLine($"f(10) = {result} (expected {45 + 1000 + 1}) from {path}, {new FileInfo(path).Length} bytes");
            return (int)result == 1046 ? 0 : 1;
        }

        private static Expression ToVoid(this Expression e) => Expression.Block(typeof(void), e);
    }
}

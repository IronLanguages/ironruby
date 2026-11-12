// Tree census: what IronRuby's expression trees embed that a saved assembly cannot contain.
//
//   IronRuby.Aot census file.rb [file.rb ...]
//
// Transforms each file the way RubyContext.CompileSourceCode does, then also transforms the
// body of every `def' (IronRuby compiles those lazily, at the first call, from the AST - they
// are not part of the file's tree at all), reduces every extension node, and counts:
//   - ConstantExpressions whose value is not an IL literal (live objects), by value type
//   - DynamicExpressions (call sites), by binder type, and whether the binder can describe
//     itself as an expression (IExpressionSerializable) and the delegate type is persistable
//   - nested lambdas, extension node types, and calls to non-public members
//     (a DynamicMethod skips visibility checks, a saved assembly does not).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Aot {
    internal sealed class Census : DynamicExpressionVisitor {
        public readonly Dictionary<string, int> LiveConstants = new();
        public readonly Dictionary<string, int> LiteralConstants = new();
        public readonly Dictionary<string, int> Binders = new();
        public readonly Dictionary<string, int> DelegateTypes = new();
        public readonly Dictionary<string, int> Extensions = new();
        public readonly Dictionary<string, int> NonPublicMembers = new();
        public int Lambdas, Nodes, MethodBodies, Sites;
        public readonly List<RubyMethodBody> Bodies = new();

        private static void Inc(Dictionary<string, int> d, string key) {
            d.TryGetValue(key, out int n);
            d[key] = n + 1;
        }

        public override Expression Visit(Expression node) {
            if (node != null) Nodes++;
            return base.Visit(node);
        }

        internal static bool IsLiteral(object value, Type type) {
            if (value == null) return true;
            switch (Type.GetTypeCode(value.GetType())) {
                case TypeCode.Boolean: case TypeCode.SByte: case TypeCode.Byte: case TypeCode.Int16:
                case TypeCode.UInt16: case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64:
                case TypeCode.UInt64: case TypeCode.Single: case TypeCode.Double: case TypeCode.String:
                case TypeCode.Char: case TypeCode.Decimal:
                    return true;
            }
            if (value is Type t && !t.IsGenericParameter && t.Assembly is not System.Reflection.Emit.AssemblyBuilder) return true;
            if (value is MethodBase m && !(m is System.Reflection.Emit.DynamicMethod)) return true;
            if (value.GetType().IsEnum) return true;
            return false;
        }

        protected override Expression VisitConstant(ConstantExpression node) {
            if (IsLiteral(node.Value, node.Type)) {
                Inc(LiteralConstants, node.Value == null ? "null" : node.Value.GetType().Name);
            } else {
                string name = node.Value.GetType().FullName;
                if (node.Value is CallSite site) {
                    name = "CallSite<" + (site.GetType().GetGenericArguments()[0].Assembly.IsDynamic ? "[dynamic assembly] " : "") + "Func`" + site.GetType().GetGenericArguments()[0].GetGenericArguments().Length + ">";
                    Inc(Binders, "(site constant) " + site.Binder.GetType().Name + (site.Binder is IExpressionSerializable ? "" : "  [NOT serializable]"));
                }
                if (node.Value is IExpressionSerializable) name += "  [IExpressionSerializable]";
                Inc(LiveConstants, name);
                if (node.Value is RubyMethodBody body) {
                    Bodies.Add(body);
                }
            }
            return node;
        }

        protected override Expression VisitDynamic(DynamicExpression node) {
            Sites++;
            string b = node.Binder.GetType().Name + (node.Binder is IExpressionSerializable ? "" : "  [NOT serializable]");
            Inc(Binders, b);
            var dt = node.DelegateType;
            bool persistable = !(dt.Assembly.IsDynamic);
            Inc(DelegateTypes, (persistable ? "" : "[dynamic assembly] ") + (dt.IsGenericType ? dt.Name + "<" + dt.GetGenericArguments().Length + ">" : dt.FullName));
            return base.VisitDynamic(node);
        }

        protected override Expression VisitLambda<T>(Expression<T> node) {
            Lambdas++;
            return base.VisitLambda(node);
        }

        protected override Expression VisitExtension(Expression node) {
            Inc(Extensions, node.GetType().Name);
            if (node.CanReduce) {
                return Visit(node.Reduce());
            }
            return node;
        }

        private void CheckAccess(MemberInfo m) {
            bool isPublic = m switch {
                MethodBase mb => mb.IsPublic && IsVisible(mb.DeclaringType),
                FieldInfo f => f.IsPublic && IsVisible(f.DeclaringType),
                PropertyInfo p => (p.GetMethod ?? p.SetMethod).IsPublic && IsVisible(p.DeclaringType),
                _ => true
            };
            if (!isPublic) Inc(NonPublicMembers, m.DeclaringType?.Name + "." + m.Name);
        }

        private static bool IsVisible(Type t) => t == null || t.IsVisible;

        protected override Expression VisitMethodCall(MethodCallExpression node) { CheckAccess(node.Method); return base.VisitMethodCall(node); }
        protected override Expression VisitMember(MemberExpression node) { CheckAccess(node.Member); return base.VisitMember(node); }
        protected override Expression VisitNew(NewExpression node) { if (node.Constructor != null) CheckAccess(node.Constructor); return base.VisitNew(node); }

        public void Print() {
            void Dump(string title, Dictionary<string, int> d) {
                Console.WriteLine($"== {title} ({d.Values.Sum()} occurrences, {d.Count} kinds)");
                foreach (var kv in d.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Value,7}  {kv.Key}");
            }
            Console.WriteLine($"nodes visited: {Nodes}, lambdas: {Lambdas}, method bodies: {MethodBodies}, dynamic sites: {Sites}");
            Dump("live-object constants (cannot be saved as-is)", LiveConstants);
            Dump("literal constants (fine)", LiteralConstants);
            Dump("call-site binders", Binders);
            Dump("call-site delegate types", DelegateTypes);
            Dump("extension nodes (reduced before counting their children)", Extensions);
            Dump("non-public members referenced (need an access-check bypass)", NonPublicMembers);
        }

        public static int Run(string[] files) {
            var context = AotHost.CreateContext(out _);
            var census = new Census();
            foreach (var file in files) {
                var lambda = AotHost.ParseFile(context, file);
                census.Visit(lambda);
            }
            // method bodies are compiled at first call; transform each one here
            var seen = new HashSet<RubyMethodBody>();
            for (int i = 0; i < census.Bodies.Count; i++) {
                var body = census.Bodies[i];
                if (!seen.Add(body)) continue;
                census.MethodBodies++;
                var bodyLambda = AotHost.TransformMethodBody(context, body);
                census.Visit(bodyLambda);
            }
            census.Print();
            return 0;
        }
    }
}

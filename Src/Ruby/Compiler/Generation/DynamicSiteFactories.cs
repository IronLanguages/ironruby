using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace IronRuby.Compiler.Generation {
    /// <summary>
    /// Runtime replacement for LambdaExpression.CompileToMethod, which is unavailable on .NET Core.
    /// Dynamic-site initializer lambdas reference static FieldBuilders of the emitted type, so they
    /// cannot be compiled at emit time. They are registered here and compiled from the emitted type's
    /// static constructor, after the runtime type (and its real FieldInfos) exist.
    /// </summary>
    public static class DynamicSiteFactories {
        private static readonly ConcurrentDictionary<int, LambdaExpression> _factories = new ConcurrentDictionary<int, LambdaExpression>();
        private static int _nextId;

        public static int Register(LambdaExpression/*!*/ factory) {
            int id = Interlocked.Increment(ref _nextId);
            _factories[id] = factory;
            return id;
        }

        [Emitted]
        public static void RunFactory(int id, RuntimeTypeHandle typeHandle) {
            LambdaExpression lambda;
            if (!_factories.TryRemove(id, out lambda)) {
                throw new InvalidOperationException("Dynamic site factory already consumed: " + id);
            }
            Type type = Type.GetTypeFromHandle(typeHandle);
            var rewritten = (LambdaExpression)new BuilderFieldRewriter(type).Visit(lambda);
            rewritten.Compile().DynamicInvoke();
        }

        private sealed class BuilderFieldRewriter : ExpressionVisitor {
            private readonly Type/*!*/ _type;

            internal BuilderFieldRewriter(Type/*!*/ type) {
                _type = type;
            }

            protected override Expression VisitMember(MemberExpression node) {
                FieldInfo field = node.Member as FieldInfo;
                if (field != null && node.Expression == null && field.DeclaringType is TypeBuilder) {
                    FieldInfo runtimeField = _type.GetField(field.Name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                    if (runtimeField != null) {
                        return Expression.Field(null, runtimeField);
                    }
                }
                return base.VisitMember(node);
            }
        }
    }
}

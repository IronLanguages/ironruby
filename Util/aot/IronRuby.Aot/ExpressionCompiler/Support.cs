// Stand-ins for the System.Core internals the ported LambdaCompiler used: resource strings,
// the stack guard, and the marker subclass of BlockExpression (whose constructor is not public).
using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;

namespace IronRuby.Aot.Compiler {
    internal static class SpilledBlocks {
        private static readonly ConditionalWeakTable<Expression, object> _spilled = new();

        public static Expression Mark(BlockExpression block) {
            _spilled.AddOrUpdate(block, null);
            return block;
        }

        public static bool IsSpilled(Expression node) => node != null && _spilled.TryGetValue(node, out _);
    }

    internal sealed class StackGuard {
        private const int MaxExecutionStackCount = 1024;
        private int _executionStackCount;

        public bool TryEnterOnCurrentStack() {
            if (RuntimeHelpers.TryEnsureSufficientExecutionStack()) return true;
            if (_executionStackCount < MaxExecutionStackCount) return false;
            throw new InsufficientExecutionStackException();
        }

        public void RunOnEmptyStack<T1, T2>(Action<T1, T2> action, T1 arg1, T2 arg2) =>
            RunOnEmptyStackCore(() => { action(arg1, arg2); return 0; });

        public void RunOnEmptyStack<T1, T2, T3>(Action<T1, T2, T3> action, T1 arg1, T2 arg2, T3 arg3) =>
            RunOnEmptyStackCore(() => { action(arg1, arg2, arg3); return 0; });

        public R RunOnEmptyStack<T1, T2, R>(Func<T1, T2, R> action, T1 arg1, T2 arg2) =>
            RunOnEmptyStackCore(() => action(arg1, arg2));

        public R RunOnEmptyStack<T1, T2, T3, R>(Func<T1, T2, T3, R> action, T1 arg1, T2 arg2, T3 arg3) =>
            RunOnEmptyStackCore(() => action(arg1, arg2, arg3));

        private R RunOnEmptyStackCore<R>(Func<R> action) {
            _executionStackCount++;
            try {
                R result = default;
                Exception error = null;
                var t = new Thread(() => { try { result = action(); } catch (Exception e) { error = e; } }, 16 * 1024 * 1024);
                t.Start();
                t.Join();
                if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                return result;
            } finally {
                _executionStackCount--;
            }
        }
    }
}

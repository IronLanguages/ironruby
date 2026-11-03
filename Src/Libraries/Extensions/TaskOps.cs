/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Task#value is Thread#value for a task: it waits, then returns the result (nil for a task without one) or
    /// raises what the task failed with. Methods are looked up on the task's class, so this also covers the
    /// internal Task&lt;T&gt; subclasses an async method returns.
    /// </summary>
    [RubyClass(Extends = typeof(Task), Restrictions = ModuleRestrictions.None)]
    [Includes(typeof(IAsyncResult), typeof(IDisposable))] // a library class does not get the CLR interfaces mixed in by itself
    public static class TaskOps {
        // task type -> its Result getter, null for a task that has no result
        private static readonly ConcurrentDictionary<Type, Func<Task, object>>/*!*/ _resultGetters =
            new ConcurrentDictionary<Type, Func<Task, object>>();

        private static readonly MethodInfo/*!*/ _getResultMethod =
            typeof(TaskOps).GetMethod("GetResult", BindingFlags.NonPublic | BindingFlags.Static);

        private static object GetResult<T>(Task/*!*/ task) {
            return ((Task<T>)task).Result;
        }

        private static Func<Task, object> MakeResultGetter(Type/*!*/ taskType) {
            for (Type type = taskType; type != null && type != typeof(Task); type = type.BaseType) {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) {
                    Type resultType = type.GetGenericArguments()[0];
                    // what an `async Task' method (or Task.CompletedTask) really is: a Task<VoidTaskResult>
                    if (resultType.FullName == "System.Threading.Tasks.VoidTaskResult") {
                        return null;
                    }
                    return (Func<Task, object>)_getResultMethod.MakeGenericMethod(resultType).CreateDelegate(typeof(Func<Task, object>));
                }
            }
            return null;
        }

        /// <summary>
        /// A faulted task raises the exception it failed with rather than the AggregateException that wraps it
        /// (unless there are several of them); a cancelled one raises its TaskCanceledException.
        /// </summary>
        [RubyMethod("value")]
        public static object GetValue(Task/*!*/ self) {
            try {
                self.Wait();
            } catch (AggregateException e) {
                if (e.InnerExceptions.Count == 1) {
                    ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
                }
                throw;
            }

            var getter = _resultGetters.GetOrAdd(self.GetType(), MakeResultGetter);
            return (getter != null) ? getter(self) : null;
        }
    }

    /// <summary>
    /// An async stream (IAsyncEnumerable&lt;T&gt;, what an `async IAsyncEnumerable' iterator returns) is Enumerable:
    /// #each drains it synchronously, waiting for each element in turn, and disposes the enumerator however the
    /// iteration ends.
    /// </summary>
    [RubyModule(Extends = typeof(IAsyncEnumerable<>), Restrictions = ModuleRestrictions.None)]
    [Includes(typeof(Enumerable))]
    public static class IAsyncEnumerableOps {
        // stream type -> each for the IAsyncEnumerable<T> it implements
        private static readonly ConcurrentDictionary<Type, Func<BlockParam, object, object>>/*!*/ _eachers =
            new ConcurrentDictionary<Type, Func<BlockParam, object, object>>();

        private static readonly MethodInfo/*!*/ _eachMethod =
            typeof(IAsyncEnumerableOps).GetMethod("EachItem", BindingFlags.NonPublic | BindingFlags.Static);

        private static object EachItem<T>(BlockParam/*!*/ block, object/*!*/ self) {
            IAsyncEnumerator<T> enumerator = ((IAsyncEnumerable<T>)self).GetAsyncEnumerator();
            try {
                while (Wait(enumerator.MoveNextAsync())) {
                    object result;
                    if (block.Yield(enumerator.Current, out result)) {
                        return result;
                    }
                }
            } finally {
                Wait(enumerator.DisposeAsync());
            }
            return self;
        }

        // GetResult on an incomplete ValueTask is not allowed; AsTask waits, and GetResult unwraps the exception
        private static bool Wait(ValueTask<bool> task) {
            return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        }

        private static void Wait(ValueTask task) {
            if (!task.IsCompletedSuccessfully) {
                task.AsTask().GetAwaiter().GetResult();
            }
        }

        private static Func<BlockParam, object, object> MakeEacher(Type/*!*/ type) {
            foreach (Type iface in type.GetInterfaces()) {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)) {
                    return (Func<BlockParam, object, object>)_eachMethod.MakeGenericMethod(iface.GetGenericArguments())
                        .CreateDelegate(typeof(Func<BlockParam, object, object>));
                }
            }
            return null;
        }

        [RubyMethod("each")]
        public static object Each(RubyContext/*!*/ context, BlockParam block, object/*!*/ self) {
            if (block == null) {
                return new Enumerator(self, "each");
            }

            // the module can also be included into a Ruby class
            var each = _eachers.GetOrAdd(self.GetType(), MakeEacher);
            if (each == null) {
                throw RubyExceptions.CreateTypeError("{0} is not an IAsyncEnumerable", context.GetClassDisplayName(self));
            }
            return each(block, self);
        }
    }
}

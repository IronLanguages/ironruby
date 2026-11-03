using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace IronRuby.Runtime {
    /// <summary>
    /// Lets a recursive walk over a syntax tree go as deep as the tree does. prism accepts nesting
    /// up to 10000 levels, and the walks that turn its tree into code recurse once (or several
    /// times) per level - deeper than a thread's stack goes. A walk asks <see cref="IsSufficient"/>
    /// before it descends, and when the stack is close to running out it carries on with
    /// <see cref="RunOnFreshStack{T}"/>: on a new thread, with a stack of its own, while this one
    /// waits. This is what System.Linq.Expressions' compiler does for deep expression trees.
    /// </summary>
    internal static class StackGuard {
        // Reserved, not committed: the pages are touched only as deep as the walk goes.
        private const int FreshStackSize = 64 * 1024 * 1024;

        /// <summary>
        /// Whether there is room on this thread's stack for another level of a walk (what the runtime
        /// considers enough to run an average .NET function).
        /// </summary>
        public static bool IsSufficient {
            get { return RuntimeHelpers.TryEnsureSufficientExecutionStack(); }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on a new thread with a fresh stack and waits for it, so that
        /// it looks to the caller like an ordinary call: its result is returned and its exception
        /// rethrown with the original stack trace.
        /// </summary>
        public static T RunOnFreshStack<T>(Func<T>/*!*/ action) {
            T result = default(T);
            ExceptionDispatchInfo error = null;
            CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
            var thread = new Thread(() => {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
                try {
                    result = action();
                } catch (Exception e) {
                    error = ExceptionDispatchInfo.Capture(e);
                }
            }, FreshStackSize);
            thread.Name = "IronRuby deep tree walk";
            thread.Start();
            thread.Join();
            if (error != null) {
                error.Throw();
            }
            return result;
        }

        public static void RunOnFreshStack(Action/*!*/ action) {
            RunOnFreshStack<object>(() => {
                action();
                return null;
            });
        }

        #region One thread with a large stack, for work that often needs one

        // Creating a thread costs too much to pay for every call of something frequent - every file prism
        // parses, say - so those calls take turns on this one.

        private sealed class LargeStackWork {
            public Func<object> Action;
            public CultureInfo Culture, UICulture;
            public object Result;
            public ExceptionDispatchInfo Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly BlockingCollection<LargeStackWork>/*!*/ _largeStackQueue = new BlockingCollection<LargeStackWork>();
        private static readonly object/*!*/ _largeStackLock = new object();
        private static Thread _largeStackThread;

        /// <summary>
        /// Runs <paramref name="action"/> on a long-lived thread with a large stack (one at a time) and waits for it,
        /// so that it looks to the caller like an ordinary call. For frequent work, where a thread per call
        /// (<see cref="RunOnFreshStack{T}"/>) would cost too much.
        /// </summary>
        public static T RunOnLargeStack<T>(Func<T>/*!*/ action) {
            if (Thread.CurrentThread == _largeStackThread) {
                return action();
            }
            EnsureLargeStackThread();

            // Each call has its own completion, so a caller interrupted while it waits leaves nothing behind
            // for the next one.
            var work = new LargeStackWork {
                Action = () => action(),
                Culture = CultureInfo.CurrentCulture,
                UICulture = CultureInfo.CurrentUICulture,
            };
            _largeStackQueue.Add(work);
            work.Done.Wait();
            if (work.Error != null) {
                work.Error.Throw();
            }
            return (T)work.Result;
        }

        private static void EnsureLargeStackThread() {
            if (_largeStackThread != null) {
                return;
            }
            lock (_largeStackLock) {
                if (_largeStackThread == null) {
                    var thread = new Thread(RunLargeStackWork, FreshStackSize);
                    thread.Name = "IronRuby large stack";
                    thread.IsBackground = true;
                    thread.Start();
                    _largeStackThread = thread;
                }
            }
        }

        private static void RunLargeStackWork() {
            foreach (LargeStackWork work in _largeStackQueue.GetConsumingEnumerable()) {
                CultureInfo.CurrentCulture = work.Culture;
                CultureInfo.CurrentUICulture = work.UICulture;
                try {
                    work.Result = work.Action();
                } catch (Exception e) {
                    work.Error = ExceptionDispatchInfo.Capture(e);
                } finally {
                    work.Done.Set();
                }
            }
        }

        #endregion
    }
}

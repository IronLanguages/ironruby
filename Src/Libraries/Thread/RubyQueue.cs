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
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.StandardLibrary.Threading {
    /// <summary>
    /// Thread::Queue - an unbounded blocking queue. Waits are Monitor waits, so Thread#kill and
    /// Thread#raise (which are delivered with Thread.Interrupt on .NET Core) unblock them.
    /// </summary>
    [RubyClass("Queue", DefineIn = typeof(ThreadOps))]
    public class RubyQueue {

        /// <summary>MRI's #&lt;Thread::Queue:0x...&gt;, rather than the CLR type name.</summary>
        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyQueue/*!*/ self) {
            return RubyUtils.ObjectToMutableString(context, self);
        }
        protected readonly Queue<object>/*!*/ _queue;
        protected int _waiting;
        protected bool _closed;

        public RubyQueue() {
            _queue = new Queue<object>();
        }

        protected RubyQueue(int capacity) {
            _queue = new Queue<object>(capacity);
        }

        internal object SyncRoot { get { return _queue; } }

        /// <summary>
        /// A snapshot of the queued values, for ObjectSpace.reachable_objects_from.
        /// </summary>
        internal object[]/*!*/ GetContents() {
            lock (_queue) {
                return _queue.ToArray();
            }
        }
        internal bool Closed { get { return _closed; } }

        protected void CheckOpen() {
            if (_closed) {
                throw new ClosedQueueError("queue closed");
            }
        }

        private void Enqueue(object value) {
            lock (_queue) {
                CheckOpen();
                _queue.Enqueue(value);
                Monitor.PulseAll(_queue);
            }
        }

        /// <summary>
        /// Blocking dequeue. Returns false via <paramref name="timedOut"/> when the wait expired or the
        /// queue was closed while empty; the result is then null, which is what Ruby returns.
        /// </summary>
        protected object Dequeue(int timeoutMilliseconds) {
            lock (_queue) {
                long deadline = timeoutMilliseconds < 0 ? 0 : Environment.TickCount64 + timeoutMilliseconds;
                _waiting++;
                try {
                    while (_queue.Count == 0) {
                        if (_closed) {
                            return null;
                        }
                        if (timeoutMilliseconds < 0) {
                            WaitOnQueue(Timeout.Infinite);
                        } else {
                            int remaining = (int)(deadline - Environment.TickCount64);
                            if (remaining <= 0 || !WaitOnQueue(remaining)) {
                                return null;
                            }
                        }
                    }
                } finally {
                    _waiting--;
                }
                object value = _queue.Dequeue();
                Monitor.PulseAll(_queue);
                return value;
            }
        }

        private bool WaitOnQueue(int milliseconds) {
            try {
                return milliseconds == Timeout.Infinite ? Monitor.Wait(_queue) : Monitor.Wait(_queue, milliseconds);
            } catch (ThreadInterruptedException) {
                RubyUtils.TranslateThreadInterrupt();
                return true;
            }
        }

        internal static int GetTimeoutMilliseconds(RubyContext/*!*/ context, object timeout) {
            if (timeout == null) {
                return Timeout.Infinite;
            }
            double seconds;
            if (timeout is int) {
                seconds = (int)timeout;
            } else if (timeout is double) {
                seconds = (double)timeout;
            } else if (timeout is System.Numerics.BigInteger) {
                seconds = (double)(System.Numerics.BigInteger)timeout;
            } else {
                // The class has to be spelled the way Ruby spells it, not the way the CLR does:
                // MutableString is String, and true/false are spelled by value.
                throw RubyExceptions.CreateImplicitConversionError(context.GetClassDisplayName(timeout), "Float");
            }
            double ms = seconds * 1000;
            return ms > Int32.MaxValue ? Timeout.Infinite : (ms < 0 ? 0 : (int)ms);
        }

        #region initialize

        [RubyConstructor]
        public static RubyQueue/*!*/ CreateQueue(RubyClass/*!*/ self) {
            return new RubyQueue();
        }

        [RubyConstructor]
        public static RubyQueue/*!*/ CreateQueue(ConversionStorage<IList>/*!*/ toA, RubyContext/*!*/ context, RubyClass/*!*/ self, object items) {
            RubyQueue result = new RubyQueue();
            Fill(toA, context, result, items);
            return result;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyQueue/*!*/ Reinitialize(RubyQueue/*!*/ self) {
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyQueue/*!*/ Reinitialize(ConversionStorage<IList>/*!*/ toA, RubyContext/*!*/ context, RubyQueue/*!*/ self, object items) {
            Fill(toA, context, self, items);
            return self;
        }

        /// <summary>
        /// MRI seeds a Queue from an Enumerable with #to_a, not with the #to_ary implicit-conversion
        /// protocol, and words the failure as an explicit conversion ("can't convert X into Array").
        /// </summary>
        private static void Fill(ConversionStorage<IList>/*!*/ toA, RubyContext/*!*/ context, RubyQueue/*!*/ queue, object items) {
            // Queue.new(nil) is an empty queue
            if (items == null) {
                return;
            }
            IList list = items as IList;
            if (list == null) {
                list = Protocols.TryConvertToArray(toA, items);
                if (list == null) {
                    throw RubyExceptions.CreateTypeConversionError(context.GetClassDisplayName(items), "Array");
                }
            }
            lock (queue._queue) {
                foreach (object item in list) {
                    queue._queue.Enqueue(item);
                }
            }
        }

        #endregion

        [RubyMethod("enq")]
        [RubyMethod("push")]
        [RubyMethod("<<")]
        public static RubyQueue/*!*/ Enqueue(RubyQueue/*!*/ self, object value) {
            self.Enqueue(value);
            return self;
        }

        [RubyMethod("deq")]
        [RubyMethod("pop")]
        [RubyMethod("shift")]
        public static object Dequeue(RubyQueue/*!*/ self, [Optional]bool nonBlocking) {
            if (nonBlocking) {
                lock (self._queue) {
                    if (self._queue.Count == 0) {
                        throw new ThreadError("queue empty");
                    }
                    object value = self._queue.Dequeue();
                    Monitor.PulseAll(self._queue);
                    return value;
                }
            }
            return self.Dequeue(Timeout.Infinite);
        }

        // Ruby 3.2's pop(timeout: seconds). IronRuby has no keyword arguments, so the caller's
        // "timeout: x" arrives as a trailing hash.
        [RubyMethod("deq")]
        [RubyMethod("pop")]
        [RubyMethod("shift")]
        public static object Dequeue(RubyContext/*!*/ context, RubyQueue/*!*/ self, [NotNull]Hash/*!*/ options) {
            return self.Dequeue(GetTimeoutMilliseconds(context, GetTimeoutOption(context, options)));
        }

        [RubyMethod("deq")]
        [RubyMethod("pop")]
        [RubyMethod("shift")]
        public static object Dequeue(RubyContext/*!*/ context, RubyQueue/*!*/ self, bool nonBlocking, [NotNull]Hash/*!*/ options) {
            if (nonBlocking && options.Count > 0) {
                throw RubyExceptions.CreateArgumentError("can't set a timeout if non_block is enabled");
            }
            return Dequeue(context, self, options);
        }

        // pop takes the non-blocking flag and the timeout: keyword, nothing more
        [RubyMethod("deq")]
        [RubyMethod("pop")]
        [RubyMethod("shift")]
        public static object Dequeue(RubyQueue/*!*/ self, object nonBlocking, object other) {
            throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 2, expected 0..1)");
        }

        internal static object GetTimeoutOption(RubyContext/*!*/ context, Hash/*!*/ options) {
            object value;
            if (options.TryGetValue(context.CreateAsciiSymbol("timeout"), out value)) {
                return value;
            }
            return null;
        }

        [RubyMethod("size")]
        [RubyMethod("length")]
        public static int GetCount(RubyQueue/*!*/ self) {
            lock (self._queue) {
                return self._queue.Count;
            }
        }

        [RubyMethod("clear")]
        public static RubyQueue/*!*/ Clear(RubyQueue/*!*/ self) {
            lock (self._queue) {
                self._queue.Clear();
                Monitor.PulseAll(self._queue);
            }
            return self;
        }

        [RubyMethod("empty?")]
        public static bool IsEmpty(RubyQueue/*!*/ self) {
            return GetCount(self) == 0;
        }

        [RubyMethod("num_waiting")]
        public static int GetNumberOfWaitingThreads(RubyQueue/*!*/ self) {
            lock (self._queue) {
                return self._waiting;
            }
        }

        [RubyMethod("close")]
        public static RubyQueue/*!*/ Close(RubyQueue/*!*/ self) {
            lock (self._queue) {
                self._closed = true;
                // everyone blocked in #pop gets nil
                Monitor.PulseAll(self._queue);
            }
            return self;
        }

        [RubyMethod("closed?")]
        public static bool IsClosed(RubyQueue/*!*/ self) {
            lock (self._queue) {
                return self._closed;
            }
        }

        [RubyMethod("freeze")]
        public static object Freeze(RubyContext/*!*/ context, RubyQueue/*!*/ self) {
            throw RubyExceptions.CreateTypeError("cannot freeze {0}",
                context.Inspect(self).ToString());
        }
    }
}

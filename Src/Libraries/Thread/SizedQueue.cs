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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Threading {
    /// <summary>
    /// Thread::SizedQueue - a Queue with an upper bound; #push blocks while the queue is full.
    /// </summary>
    [RubyClass(DefineIn = typeof(ThreadOps))]
    public class SizedQueue : RubyQueue {

        /// <summary>MRI's #&lt;Thread::SizedQueue:0x...&gt;, rather than the CLR type name.</summary>
        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, SizedQueue/*!*/ self) {
            return RubyUtils.ObjectToMutableString(context, self);
        }
        private int _limit = 1;

        public SizedQueue([DefaultProtocol]int limit) {
            SetLimitChecked(limit);
        }

        private void SetLimitChecked(int limit) {
            if (limit <= 0) {
                throw RubyExceptions.CreateArgumentError("queue size must be positive");
            }
            _limit = limit;
        }

        /// <summary>Returns false if the wait timed out (and nothing was queued).</summary>
        private bool Enqueue(object value, int timeoutMilliseconds) {
            lock (_queue) {
                CheckOpen();
                long deadline = timeoutMilliseconds < 0 ? 0 : Environment.TickCount64 + timeoutMilliseconds;
                _waiting++;
                try {
                    while (_queue.Count >= _limit) {
                        bool signalled;
                        try {
                            if (timeoutMilliseconds < 0) {
                                signalled = Monitor.Wait(_queue);
                            } else {
                                int remaining = (int)(deadline - Environment.TickCount64);
                                signalled = remaining > 0 && Monitor.Wait(_queue, remaining);
                            }
                        } catch (ThreadInterruptedException) {
                            RubyUtils.TranslateThreadInterrupt();
                            continue;
                        }
                        CheckOpen();
                        if (!signalled) {
                            return false;
                        }
                    }
                } finally {
                    _waiting--;
                }
                _queue.Enqueue(value);
                Monitor.PulseAll(_queue);
                return true;
            }
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static SizedQueue/*!*/ Reinitialize(SizedQueue/*!*/ self, [DefaultProtocol]int limit) {
            self.SetLimitChecked(limit);
            return self;
        }

        [RubyMethod("max")]
        public static int GetLimit(SizedQueue/*!*/ self) {
            return self._limit;
        }

        [RubyMethod("max=")]
        public static int SetLimit(SizedQueue/*!*/ self, [DefaultProtocol]int limit) {
            lock (self._queue) {
                self.SetLimitChecked(limit);
                Monitor.PulseAll(self._queue);
            }
            return limit;
        }

        [RubyMethod("enq")]
        [RubyMethod("push")]
        [RubyMethod("<<")]
        public static SizedQueue/*!*/ Enqueue(SizedQueue/*!*/ self, object value, [Optional]bool nonBlocking) {
            if (nonBlocking) {
                lock (self._queue) {
                    self.CheckOpen();
                    if (self._queue.Count >= self._limit) {
                        throw new ThreadError("queue full");
                    }
                    self._queue.Enqueue(value);
                    Monitor.PulseAll(self._queue);
                }
                return self;
            }
            self.Enqueue(value, Timeout.Infinite);
            return self;
        }

        // SizedQueue#push(value, timeout: seconds); IronRuby has no keyword arguments, so it arrives
        // as a trailing hash.
        [RubyMethod("enq")]
        [RubyMethod("push")]
        [RubyMethod("<<")]
        public static object Enqueue(RubyContext/*!*/ context, SizedQueue/*!*/ self, object value, [NotNull]Hash/*!*/ options) {
            int ms = RubyQueue.GetTimeoutMilliseconds(context, RubyQueue.GetTimeoutOption(context, options));
            return self.Enqueue(value, ms) ? (object)self : null;
        }

        [RubyMethod("enq")]
        [RubyMethod("push")]
        [RubyMethod("<<")]
        public static object Enqueue(RubyContext/*!*/ context, SizedQueue/*!*/ self, object value, bool nonBlocking,
            [NotNull]Hash/*!*/ options) {

            if (nonBlocking && options.Count > 0) {
                throw RubyExceptions.CreateArgumentError("can't set a timeout if non_block is enabled");
            }
            return Enqueue(context, self, value, options);
        }
    }
}

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

namespace IronRuby.StandardLibrary.Threading {
    /// <summary>
    /// Thread::ConditionVariable. Built on the same per-thread sleep signal as Thread.stop, exactly like
    /// MRI: that is what makes Thread#run and Thread#wakeup end a #wait (a Monitor condition could not
    /// be woken that way), and it gives #signal the FIFO order the specs require. The old implementation
    /// used an AutoResetEvent plus a 1ms sleep per waiter in #broadcast and lost wakeups.
    /// </summary>
    [RubyClass("ConditionVariable", DefineIn = typeof(ThreadOps))]
    public class RubyConditionVariable {

        /// <summary>MRI's #&lt;Thread::ConditionVariable:0x...&gt;, rather than the CLR type name.</summary>
        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyConditionVariable/*!*/ self) {
            return RubyUtils.ObjectToMutableString(context, self);
        }
        private readonly object/*!*/ _lock = new object();
        private readonly LinkedList<IronRuby.Builtins.ThreadOps.RubyThreadInfo>/*!*/ _waiters = new LinkedList<IronRuby.Builtins.ThreadOps.RubyThreadInfo>();

        public RubyConditionVariable() {
        }

        // #signal and #broadcast wake a waiter under _lock, together with taking it off the list, so
        // that a waiter which finds itself off the list knows the wake has already been sent.
        [RubyMethod("signal")]
        public static RubyConditionVariable/*!*/ Signal(RubyConditionVariable/*!*/ self) {
            lock (self._lock) {
                if (self._waiters.Count > 0) {
                    var first = self._waiters.First.Value;
                    self._waiters.RemoveFirst();
                    first.Wake();
                }
            }
            return self;
        }

        [RubyMethod("broadcast")]
        public static RubyConditionVariable/*!*/ Broadcast(RubyConditionVariable/*!*/ self) {
            lock (self._lock) {
                foreach (var waiter in self._waiters) {
                    waiter.Wake();
                }
                self._waiters.Clear();
            }
            return self;
        }

        /// <summary>
        /// MRI's ConditionVariable#wait does not require a Mutex: it releases the lock by calling
        /// #unlock on whatever it was handed, sleeps with #sleep, and re-locks with #lock. Only the
        /// real Mutex takes the fast path below; anything else is driven through those three calls.
        /// </summary>
        [RubyMethod("wait")]
        public static object Wait(BinaryOpStorage/*!*/ sleepStorage, RubyContext/*!*/ context, RubyConditionVariable/*!*/ self,
            object mutex, [Optional]object timeout) {

            // MRI answers what mutex.sleep answers: the seconds slept, or nil on a timeout
            var site = sleepStorage.GetCallSite("sleep", 1);
            return site.Target(site, mutex, timeout);
        }

        [RubyMethod("wait")]
        public static object Wait(RubyConditionVariable/*!*/ self, [NotNull]RubyMutex/*!*/ mutex) {
            return DoWait(self, mutex, Double.NegativeInfinity);
        }

        [RubyMethod("wait")]
        public static object Wait(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ divmodStorage,
            BinaryOpStorage/*!*/ multiplyStorage, ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<IntegerValue>/*!*/ integerCast,
            RubyConditionVariable/*!*/ self, [NotNull]RubyMutex/*!*/ mutex, object timeout) {

            // MRI's #wait is mutex.sleep(timeout): the same conversion, and the same errors
            return DoWait(self, mutex,
                RubyMutex.ToSleepSeconds(respondToStorage, divmodStorage, multiplyStorage, arrayTryCast, integerCast, timeout));
        }

        private static object DoWait(RubyConditionVariable/*!*/ self, RubyMutex/*!*/ mutex, double seconds) {
            int ms;
            if (Double.IsNegativeInfinity(seconds)) {
                ms = Timeout.Infinite;
            } else {
                double milliseconds = seconds * 1000;
                ms = milliseconds > Int32.MaxValue ? Timeout.Infinite : (int)milliseconds;
            }

            var info = IronRuby.Builtins.ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
            LinkedListNode<IronRuby.Builtins.ThreadOps.RubyThreadInfo> node;

            // MRI: the seconds slept, rounded, when signalled; nil when the timeout ran out
            object result = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // The mutex is released and the thread queued in one step under _lock: no #signal can come
            // between them, so none is missed, and a mutex that is not ours to release (a ThreadError)
            // leaves nothing queued for a #signal to be lost on.
            lock (self._lock) {
                RubyMutex.Unlock(mutex);
                node = self._waiters.AddLast(info);
            }
            try {
                if (ms < 0) {
                    info.Sleep();
                    result = (int)Math.Round(watch.Elapsed.TotalSeconds);
                } else {
                    result = info.Sleep(ms) ? (object)(int)Math.Round(watch.Elapsed.TotalSeconds) : null;
                }
            } finally {
                bool signalled;
                lock (self._lock) {
                    signalled = node.List == null;
                    if (!signalled) {
                        self._waiters.Remove(node);
                    }
                }
                if (signalled) {
                    // A #signal picked this thread. If the timeout ran out first the wake is still
                    // latched; take it as this wait's (MRI's waiter is woken, not timed out, and the
                    // signal is not lost) and clear it so the thread's next sleep is not cut short.
                    info.ClearWake();
                    if (result == null) {
                        result = (int)Math.Round(watch.Elapsed.TotalSeconds);
                    }
                }
                // Uninterruptibly: MRI promises the mutex is held again when #wait returns, even
                // if the thread was killed while it was waiting to get it back.
                RubyMutex.DoLock(mutex, false);
            }
            return result;
        }

        [RubyMethod("marshal_dump")]
        public static object MarshalDump(RubyContext/*!*/ context, RubyConditionVariable/*!*/ self) {
            throw RubyExceptions.CreateTypeError("can't dump {0}", context.GetClassDisplayName(self));
        }
    }
}

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
using System.Numerics;
using System.Threading;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.StandardLibrary.Threading {
    /// <summary>
    /// Ruby's Mutex is not a CLR monitor: it is not recursive (a second lock from the owner is a
    /// ThreadError, not a re-entry), it may only be unlocked by its owner, and ownership belongs to a
    /// *Ruby thread* - which for a fiber means the thread that owns the fiber group, because IronRuby
    /// gives every fiber its own CLR thread.
    /// </summary>
    [RubyClass("Mutex", DefineIn = typeof(ThreadOps))]
    public class RubyMutex {

        /// <summary>MRI's #&lt;Thread::Mutex:0x...&gt;, rather than the CLR type name.</summary>
        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyMutex/*!*/ self) {
            return RubyUtils.ObjectToMutableString(context, self);
        }
        private readonly object/*!*/ _syncRoot = new object();
        // The Ruby thread that holds the lock (the fiber group's owner) ...
        private Thread _owner;
        // ... and the CLR thread that actually took it. Ruby reports #owned? per *fiber* even though
        // deadlock detection is per thread, and a fiber is a CLR thread here.
        private Thread _ownerFiber;

        // Every locked mutex is registered so that a thread's locks can be released when it dies,
        // which is what MRI does.
        private static readonly HashSet<RubyMutex>/*!*/ _lockedMutexes = new HashSet<RubyMutex>();

        public RubyMutex() {
        }

        internal object Mutex { get { return _syncRoot; } }

        private static Thread/*!*/ CurrentOwner {
            get { return RubyUtils.CurrentRubyThread; }
        }

        [RubyMethod("locked?")]
        public static bool IsLocked(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                return self._owner != null;
            }
        }

        [RubyMethod("owned?")]
        public static bool IsOwned(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                return self._ownerFiber == Thread.CurrentThread;
            }
        }

        [RubyMethod("try_lock")]
        public static bool TryLock(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                if (self._owner != null) {
                    return false;
                }
                self.Acquire();
                return true;
            }
        }

        [RubyMethod("lock")]
        public static RubyMutex/*!*/ Lock(RubyMutex/*!*/ self) {
            DoLock(self);
            return self;
        }

        private static void DoLock(RubyMutex/*!*/ self) {
            DoLock(self, true);
        }

        /// <summary>
        /// <paramref name="interruptible"/> false keeps a parked Thread#kill / Thread#raise parked
        /// until the lock has been taken. ConditionVariable#wait needs that: MRI guarantees the
        /// mutex is held again when #wait returns, even when the thread was killed while it was
        /// waiting for it, and code in an ensure clause relies on still owning it.
        /// </summary>
        internal static void DoLock(RubyMutex/*!*/ self, bool interruptible) {
            if (interruptible) {
                RubyUtils.CheckAsyncException();
            }
            Thread me = CurrentOwner;
            lock (self._syncRoot) {
                if (self._owner == me) {
                    // A fiber is a CLR thread here. If an asynchronous Thread#raise terminated
                    // the fiber in the narrow hand-off around #synchronize, reclaim its lock;
                    // a suspended (and therefore still alive) sibling fiber must still deadlock.
                    if (self._ownerFiber != null && !self._ownerFiber.IsAlive) {
                        self.Release();
                    } else {
                        throw new ThreadError("deadlock; recursive locking");
                    }
                }
                while (self._owner != null) {
                    try {
                        Monitor.Wait(self._syncRoot);
                    } catch (ThreadInterruptedException) {
                        // Thread#kill / Thread#raise nudged us; if nothing is parked the wait restarts.
                        if (interruptible) {
                            RubyUtils.TranslateThreadInterrupt();
                        }
                    }
                }
                self.Acquire();
            }
        }

        private void Acquire() {
            _owner = CurrentOwner;
            _ownerFiber = Thread.CurrentThread;
            lock (_lockedMutexes) {
                _lockedMutexes.Add(this);
            }
        }

        private void Release() {
            _owner = null;
            _ownerFiber = null;
            lock (_lockedMutexes) {
                _lockedMutexes.Remove(this);
            }
            Monitor.PulseAll(_syncRoot);
        }

        /// <summary>
        /// Called when a Ruby thread terminates: MRI releases every mutex the thread still holds.
        /// </summary>
        public static void ReleaseLocksOf(Thread/*!*/ thread) {
            RubyMutex[] held;
            lock (_lockedMutexes) {
                held = new RubyMutex[_lockedMutexes.Count];
                _lockedMutexes.CopyTo(held);
            }
            foreach (RubyMutex m in held) {
                lock (m._syncRoot) {
                    if (m._owner == thread || m._ownerFiber == thread) {
                        m.Release();
                    }
                }
            }
        }

        /// <summary>Releases the lock if the current Ruby thread holds it; false if it does not.</summary>
        private static bool DoUnlock(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                if (self._owner != CurrentOwner) {
                    return false;
                }
                self.Release();
                return true;
            }
        }

        [RubyMethod("unlock")]
        public static RubyMutex/*!*/ Unlock(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                if (self._owner == null) {
                    throw new ThreadError("Attempt to unlock a mutex which is not locked");
                }
                if (self._owner != CurrentOwner) {
                    throw new ThreadError("Attempt to unlock a mutex which is locked by another thread");
                }
                self.Release();
            }
            return self;
        }

        [RubyMethod("synchronize")]
        public static object Synchronize(BlockParam criticalSection, RubyMutex/*!*/ self) {
            if (criticalSection == null) {
                throw new ThreadError("must be called with a block");
            }
            bool ownedBefore = IsOwned(self);
            try {
                DoLock(self);
                object result;
                criticalSection.Yield(out result);
                return result;
            } finally {
                // An asynchronous Thread#raise can arrive just as DoLock completes. Release a
                // lock acquired by this call even if control never reached the block. Do not,
                // however, unlock a recursive lock that was already owned on entry.
                if (!ownedBefore && IsOwned(self)) {
                    DoUnlock(self);
                }
            }
        }

        // MRI: the seconds slept, rounded, when woken; nil when the timeout ran out.
        [RubyMethod("sleep")]
        public static object Sleep(RubyMutex/*!*/ self) {
            return DoSleep(self, Double.NegativeInfinity);
        }

        [RubyMethod("sleep")]
        public static object Sleep(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ divmodStorage,
            BinaryOpStorage/*!*/ multiplyStorage, ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<IntegerValue>/*!*/ integerCast,
            RubyMutex/*!*/ self, object timeout) {
            return DoSleep(self, ToSleepSeconds(respondToStorage, divmodStorage, multiplyStorage, arrayTryCast, integerCast, timeout));
        }

        /// <summary>
        /// The timeout of #sleep in seconds, Double.NegativeInfinity for nil (no timeout). Also used by
        /// ConditionVariable#wait, which in MRI is mutex.sleep(timeout) and so converts the same way.
        /// </summary>
        internal static double ToSleepSeconds(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ divmodStorage,
            BinaryOpStorage/*!*/ multiplyStorage, ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<IntegerValue>/*!*/ integerCast,
            object timeout) {

            double seconds;
            if (timeout == null) {
                return Double.NegativeInfinity;
            } else if (timeout is int) {
                seconds = (int)timeout;
            } else if (timeout is long) {
                seconds = (long)timeout;
            } else if (timeout is double) {
                seconds = (double)timeout;
            } else if (timeout is BigInteger) {
                seconds = ToWholeSeconds(integerCast, timeout);
            } else {
                seconds = ToSecondsByDivmod(respondToStorage, divmodStorage, multiplyStorage, arrayTryCast, integerCast, timeout);
            }
            if (Double.IsNaN(seconds)) {
                throw RubyExceptions.CreateRangeError("NaN out of Time range");
            }
            if (Double.IsPositiveInfinity(seconds)) {
                throw RubyExceptions.CreateRangeError("Inf out of Time range");
            }
            if (seconds < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }
            return seconds;
        }

        /// <summary>
        /// MRI's interval of any other object - a Rational, a BigDecimal, a Numeric of the program's own:
        /// <c>whole, fraction = timeout.divmod(1)</c>, then <c>fraction * 1_000_000_000</c> nanoseconds.
        /// Something without a #divmod that answers an Array is not an interval at all.
        /// </summary>
        private static double ToSecondsByDivmod(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ divmodStorage,
            BinaryOpStorage/*!*/ multiplyStorage, ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<IntegerValue>/*!*/ integerCast,
            object timeout) {

            IList parts = null;
            if (Protocols.RespondTo(respondToStorage, timeout, "divmod")) {
                var divmod = divmodStorage.GetCallSite("divmod");
                parts = Protocols.TryCastToArray(arrayTryCast, divmod.Target(divmod, timeout, ScriptingRuntimeHelpers.Int32ToObject(1)));
            }
            if (parts == null) {
                throw RubyExceptions.CreateTypeError("can't convert {0} into time interval",
                    respondToStorage.Context.GetClassDisplayName(timeout));
            }

            double whole = ToWholeSeconds(integerCast, parts.Count > 0 ? parts[0] : null);
            if (whole < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }
            var multiply = multiplyStorage.GetCallSite("*");
            double nanoseconds = ToWholeSeconds(integerCast,
                multiply.Target(multiply, parts.Count > 1 ? parts[1] : null, ScriptingRuntimeHelpers.Int32ToObject(1000000000)));
            return whole + nanoseconds / 1e9;
        }

        /// <summary>MRI's NUM2TIMET/NUM2LONG: a Float truncated, anything else through #to_int, into a long.</summary>
        private static double ToWholeSeconds(ConversionStorage<IntegerValue>/*!*/ integerCast, object value) {
            if (value is int) {
                return (int)value;
            } else if (value is long) {
                return (long)value;
            } else if (value is double) {
                return Math.Truncate((double)value);
            }
            IntegerValue integer = value is BigInteger ? new IntegerValue((BigInteger)value) : Protocols.CastToInteger(integerCast, value);
            long result;
            if (integer.IsFixnum) {
                return integer.Fixnum;
            } else if (integer.Bignum.AsInt64(out result)) {
                return result;
            }
            throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long'");
        }

        private static object DoSleep(RubyMutex/*!*/ self, double seconds) {
            lock (self._syncRoot) {
                if (self._owner != CurrentOwner) {
                    throw new ThreadError("Attempt to unlock a mutex which is not locked");
                }
            }

            Unlock(self);
            try {
                var info = IronRuby.Builtins.ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (Double.IsNegativeInfinity(seconds)) {
                    info.Sleep();
                } else {
                    double ms = seconds * 1000;
                    if (!info.Sleep(ms > Int32.MaxValue ? Timeout.Infinite : (int)ms)) {
                        return null;
                    }
                }
                return (int)Math.Round(watch.Elapsed.TotalSeconds);
            } finally {
                DoLock(self);
            }
        }

        [RubyMethod("exclusive_unlock")]
        public static object ExclusiveUnlock(BlockParam criticalSection, RubyMutex/*!*/ self) {
            Unlock(self);
            try {
                object result;
                criticalSection.Yield(out result);
                return result;
            } finally {
                DoLock(self);
            }
        }
    }
}

/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.StandardLibrary.Threading;
using Microsoft.Scripting.Runtime;

// Not "...Monitor": a namespace of that name would hide System.Threading.Monitor from every
// sibling namespace under IronRuby.StandardLibrary.
namespace IronRuby.StandardLibrary.Monitors {
    /// <summary>
    /// MRI's Monitor (ext/monitor/monitor.c): a reentrant lock owned by a fiber, made of a
    /// Thread::Mutex plus an entry count. MonitorMixin and MonitorMixin::ConditionVariable stay in Ruby
    /// (Src/StdLib/ironruby/monitor.rb), as in MRI.
    ///
    /// Concurrent::Map (through IronRuby's GemCompat patch of its SynchronizedMapBackend), Rails'
    /// connection pool and ActionDispatch::Response synchronize on a Monitor on every access, so
    /// #synchronize is one library call: enter, yield, exit - and, like MRI's C implementation, it
    /// enters and exits directly, not through a redefinable #enter / #exit.
    ///
    /// The owner is the fiber - here, the CLR thread - that holds the mutex, which is what MRI's
    /// owner is as well; reading it off the mutex keeps the two from disagreeing when the mutex is
    /// released for a thread or fiber that died holding it.
    /// </summary>
    [RubyClass("Monitor")]
    public class RubyMonitor {
        private readonly RubyMutex/*!*/ _mutex = new RubyMutex();

        // Entries by the owner; read and written only while the mutex is held.
        private long _count;

        public RubyMonitor() {
        }

        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyMonitor/*!*/ self) {
            return RubyUtils.ObjectToMutableString(context, self);
        }

        [RubyMethod("try_enter")]
        public static bool TryEnter(RubyMonitor/*!*/ self) {
            if (!self._mutex.IsHeldByCurrentFiber) {
                if (!RubyMutex.TryLock(self._mutex)) {
                    return false;
                }
                self._count = 0;
            }
            self._count++;
            return true;
        }

        [RubyMethod("enter")]
        public static void Enter(RubyMonitor/*!*/ self) {
            if (!self._mutex.IsHeldByCurrentFiber) {
                RubyMutex.DoLock(self._mutex, true);
                self._count = 0;
            }
            self._count++;
        }

        [RubyMethod("exit")]
        public static void Exit(RubyMonitor/*!*/ self) {
            CheckOwner(self);
            if (self._count <= 0) {
                throw new ThreadError("monitor_exit: count:" + self._count);
            }
            if (--self._count == 0) {
                RubyMutex.Unlock(self._mutex);
            }
        }

        [RubyMethod("synchronize")]
        public static object Synchronize(BlockParam block, RubyMonitor/*!*/ self) {
            Enter(self);
            try {
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }
                object result;
                block.Yield(out result);
                return result;
            } finally {
                Exit(self);
            }
        }

        [RubyMethod("mon_locked?")]
        public static bool IsLocked(RubyMonitor/*!*/ self) {
            return self._mutex.IsLockedInternal;
        }

        [RubyMethod("mon_owned?")]
        public static bool IsOwned(RubyMonitor/*!*/ self) {
            return self._mutex.IsHeldByCurrentFiber;
        }

        [RubyMethod("mon_check_owner")]
        public static object CheckOwner(RubyMonitor/*!*/ self) {
            if (!self._mutex.IsHeldByCurrentFiber) {
                throw new ThreadError("current fiber not owner");
            }
            return null;
        }

        /// <summary>
        /// Releases the monitor for the duration of <c>cond.wait(mutex, timeout)</c> and takes it back,
        /// at the depth it was held, once the wait is over; answers whether the wait was signalled
        /// (cond.wait answered something truthy), as MRI does.
        /// </summary>
        [RubyMethod("wait_for_cond")]
        public static bool WaitForCond(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ waitStorage,
            RubyMonitor/*!*/ self, object cond, object timeout) {

            long count = self._count;
            self._count = 0;
            try {
                var site = waitStorage.GetCallSite("wait", 2);
                return RubyOps.IsTrue(site.Target(site, cond, self._mutex, timeout));
            } finally {
                // cond.wait takes the mutex back before it returns or raises
                self._count = count;
            }
        }
    }
}

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
using System.Threading;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime {
    /// <summary>
    /// Queryable recursive lock.
    /// </summary>
    internal sealed class CheckedMonitor {
        internal void Enter(ref bool lockTaken) {
            MonitorUtils.Enter(this, ref lockTaken);
        }

        internal void Exit(ref bool lockTaken) {
            MonitorUtils.Exit(this, ref lockTaken);
        }

        /// <summary>
        /// True if the calling thread holds this lock.  Ask the monitor itself rather than keeping a
        /// count on the side: a counter maintained around Enter/Exit is written by the leaving thread
        /// after it has already released the monitor, so two threads handing the lock over can lose one
        /// of the two updates and leave the count at zero while a thread is demonstrably inside.  It
        /// also answered for any thread rather than the asking one, which is not the question callers
        /// are asking.
        /// </summary>
        public bool IsLocked {
            get { return Monitor.IsEntered(this); }
        }

        public IDisposable/*!*/ CreateLocker() {
            return new CheckedMonitorLocker(this);
        }

        public IDisposable/*!*/ CreateUnlocker() {
            return new CheckedMonitorUnlocker(this);
        }

        private struct CheckedMonitorLocker : IDisposable {
            private readonly CheckedMonitor/*!*/ _monitor;
            private bool _lockTaken;

            public CheckedMonitorLocker(CheckedMonitor/*!*/ monitor) {
                _monitor = monitor;
                _lockTaken = false;
                monitor.Enter(ref _lockTaken);
            }

            public void Dispose() {
                _monitor.Exit(ref _lockTaken);
            }
        }

        private struct CheckedMonitorUnlocker : IDisposable {
            private readonly CheckedMonitor/*!*/ _monitor;
            private bool _lockTaken;

            public CheckedMonitorUnlocker(CheckedMonitor/*!*/ monitor) {
                _monitor = monitor;
                _lockTaken = true;
                monitor.Exit(ref _lockTaken);
            }

            public void Dispose() {
                _monitor.Enter(ref _lockTaken);
            }
        }
    }
}

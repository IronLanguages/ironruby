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
using System.Diagnostics;
using System.IO;
using System.Threading;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    /// <summary>
    /// Pipe for intra-process producer-consumer style message passing
    /// </summary>
    internal class RubyPipe : Stream {
        private readonly EventWaitHandle _dataAvailableEvent;
        private readonly EventWaitHandle _spaceAvailableEvent;
        private readonly EventWaitHandle _writerClosedEvent;
        private readonly EventWaitHandle _readerClosedEvent;
        private readonly WaitHandle[] _eventArray;
        private readonly WaitHandle[] _writeEventArray;
        private readonly Queue<byte> _queue;

        // The threads parked in IO.select (or IO#wait, or NIO::Selector#select) on this pipe.
        // There is no descriptor for the kernel to watch, so the pipe tells them itself when a
        // write, a read or a close may have changed what they are waiting for.
        private readonly List<ReadinessWaiter> _waiters;

        private const int WriterClosedEventIndex = 1;
        private const int ReaderClosedEventIndex = 2;

        /// <summary>
        /// How much the pipe holds before a write has to wait for the reader, which is the 64 KB
        /// Linux gives a pipe. The queue used to be unbounded, which is comfortable but means a
        /// write can never fail for want of room - so #write_nonblock had nothing to report
        /// EAGAIN about, and the loop ruby/spec uses to fill a pipe never ended.
        /// </summary>
        internal const int Capacity = 65536;

        private RubyPipe() {
            _dataAvailableEvent = new AutoResetEvent(false);
            _spaceAvailableEvent = new AutoResetEvent(false);
            _writerClosedEvent = new ManualResetEvent(false);
            _readerClosedEvent = new ManualResetEvent(false);
            _eventArray = new WaitHandle[3];
            _queue = new Queue<byte>();
            _waiters = new List<ReadinessWaiter>();

            _eventArray[0] = _dataAvailableEvent;
            _eventArray[1] = _writerClosedEvent;
            _eventArray[2] = _readerClosedEvent;
            Debug.Assert(_eventArray[WriterClosedEventIndex] == _writerClosedEvent);
            Debug.Assert(_eventArray[ReaderClosedEventIndex] == _readerClosedEvent);

            // A writer waiting for room wakes on the reader taking bytes out, and on the reader
            // going away - at which point the write is an EPIPE rather than a longer wait.
            _writeEventArray = new WaitHandle[] { _spaceAvailableEvent, _readerClosedEvent };
        }

        private RubyPipe(RubyPipe pipe) {
            _dataAvailableEvent = pipe._dataAvailableEvent;
            _spaceAvailableEvent = pipe._spaceAvailableEvent;
            _writerClosedEvent = pipe._writerClosedEvent;
            _readerClosedEvent = pipe._readerClosedEvent;
            _eventArray = pipe._eventArray;
            _writeEventArray = pipe._writeEventArray;
            _queue = pipe._queue;
            _waiters = pipe._waiters;
        }

        internal void CloseWriter() {
            _writerClosedEvent.Set();
            NotifyWaiters();
        }

        internal void AddWaiter(ReadinessWaiter/*!*/ waiter) {
            lock (_waiters) {
                _waiters.Add(waiter);
            }
        }

        internal void RemoveWaiter(ReadinessWaiter/*!*/ waiter) {
            lock (_waiters) {
                _waiters.Remove(waiter);
            }
        }

        private void NotifyWaiters() {
            lock (_waiters) {
                for (int i = 0; i < _waiters.Count; i++) {
                    _waiters[i].Signal();
                }
            }
        }

        /// <summary>
        /// Whether a Read would return rather than block: there are bytes queued, or the writer is
        /// gone and the read is an immediate end of file. This is what IO.select asks of a pipe,
        /// there being no kernel descriptor to poll.
        /// </summary>
        internal bool CanReadWithoutBlocking {
            get {
                lock (((ICollection)_queue).SyncRoot) {
                    return _queue.Count > 0 || _writerClosedEvent.WaitOne(0) || _readerClosedEvent.WaitOne(0);
                }
            }
        }

        /// <summary>
        /// Whether a Write would return rather than wait: there is room in the queue, or the
        /// reader is gone and the write fails at once. The counterpart to CanReadWithoutBlocking,
        /// and what IO.select and IO#wait ask of a pipe's write end.
        /// </summary>
        internal bool CanWriteWithoutBlocking {
            get {
                lock (((ICollection)_queue).SyncRoot) {
                    return _queue.Count < Capacity || _readerClosedEvent.WaitOne(0);
                }
            }
        }

        /// <summary>
        /// Writes as much as there is room for and returns how much that was - zero when the pipe
        /// is full. This is the shape of a write(2) on a non-blocking descriptor, and
        /// #write_nonblock turns the zero into EAGAIN.
        /// </summary>
        internal int WriteWithoutWaiting(byte[]/*!*/ buffer, int offset, int count) {
            if (_readerClosedEvent.WaitOne(0)) {
                throw new Errno.PipeError();
            }

            int written;
            lock (((ICollection)_queue).SyncRoot) {
                written = Math.Min(count, Capacity - _queue.Count);
                for (int i = 0; i < written; i++) {
                    _queue.Enqueue(buffer[offset + i]);
                }
                if (written > 0) {
                    _dataAvailableEvent.Set();
                }
            }
            if (written > 0) {
                NotifyWaiters();
            }
            return written;
        }

        internal void CloseReader() {
            // Wakes up a thread parked in Read so that closing the read end of a pipe from another
            // thread terminates the blocked read, the way CRuby does.
            _readerClosedEvent.Set();
            NotifyWaiters();
        }

        /// <summary>
        /// Called when this end of the pipe is closed. The reader and the writer are distinct objects
        /// so that each end can signal the other; see PipeWriter.
        /// </summary>
        protected virtual void OnClose() {
            CloseReader();
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                OnClose();
            }
            base.Dispose(disposing);
        }

        public static void CreatePipe(out Stream reader, out Stream writer) {
            RubyPipe pipe = new RubyPipe();
            reader = pipe;
            writer = new PipeWriter(pipe);
        }

        public override bool CanRead {
            get { return true; }
        }

        public override bool CanSeek {
            get { throw new NotImplementedException(); }
        }

        public override bool CanWrite {
            get { return true; }
        }

        // IO#timeout: how long a read or a write waits for the other end, per end of the pipe.
        private int _readTimeout = Timeout.Infinite;
        private int _writeTimeout = Timeout.Infinite;

        public override bool CanTimeout {
            get { return true; }
        }

        public override int ReadTimeout {
            get { return _readTimeout; }
            set { _readTimeout = value; }
        }

        public override int WriteTimeout {
            get { return _writeTimeout; }
            set { _writeTimeout = value; }
        }

        public override void Flush() {
            throw new NotImplementedException();
        }

        public override long Length {
            get { throw new NotImplementedException(); }
        }

        public override long Position {
            get {
                throw new NotImplementedException();
            }
            set {
                throw new NotImplementedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) {
            if (count == 0) {
                return 0;
            }

            while (true) {
                int read = 0;
                lock (((ICollection)_queue).SyncRoot) {
                    if (_queue.Count > 0) {
                        read = Math.Min(count, _queue.Count);
                        for (int i = 0; i < read; i++) {
                            buffer[offset + i] = _queue.Dequeue();
                        }
                        if (_queue.Count > 0) {
                            // _dataAvailableEvent is an AutoResetEvent, so re-arm it for the bytes we left behind.
                            _dataAvailableEvent.Set();
                        }
                        // Room has just appeared, so a writer parked on a full pipe can carry on.
                        _spaceAvailableEvent.Set();
                    } else if (_writerClosedEvent.WaitOne(0)) {
                        // Writer is gone and the queue is drained: end of file.
                        return 0;
                    }
                }

                if (read > 0) {
                    // ... and so can a select waiting for the write end to become writable.
                    NotifyWaiters();
                    return read;
                }

                // Wait until data is available, the writer closes the pipe, or this end is closed
                // from another thread.
                int signaled;
                using (RubyUtils.BlockingWait.Enter()) {
                    signaled = WaitHandle.WaitAny(_eventArray, _readTimeout);
                }
                if (signaled == WaitHandle.WaitTimeout) {
                    throw new TimeoutException();
                }
                if (signaled == ReaderClosedEventIndex) {
                    throw RubyExceptions.CreateIOError("stream closed in another thread");
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotImplementedException();
        }

        public override void SetLength(long value) {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            if (_readerClosedEvent.WaitOne(0)) {
                // Nobody will ever read this again. Without the error a `loop { w.write(...) }`
                // against a closed read end runs forever and grows the queue until memory runs out;
                // MRI reports EPIPE (or dies of SIGPIPE) on the first such write.
                throw new Errno.PipeError();
            }

            // More than the pipe holds goes in instalments, waiting for the reader in between,
            // which is what a write(2) larger than the kernel's pipe buffer does.
            int remaining = count;
            while (remaining > 0) {
                remaining -= WriteWithoutWaiting(buffer, offset + count - remaining, remaining);
                if (remaining > 0) {
                    int signaled;
                    using (RubyUtils.BlockingWait.Enter()) {
                        signaled = WaitHandle.WaitAny(_writeEventArray, _writeTimeout);
                    }
                    if (signaled == WaitHandle.WaitTimeout) {
                        throw new TimeoutException();
                    }
                }
            }
        }

        /// <summary>
        /// PipeWriter instance always exists as a sibling of a RubyPipe. Two objects are needed
        /// so that we can detect whether Close is being called on the reader end of a pipe,
        /// or on the writer end of a pipe.
        /// </summary>
        internal class PipeWriter : RubyPipe {
            internal PipeWriter(RubyPipe pipe)
                : base(pipe) {
            }

            protected override void OnClose() {
                CloseWriter();
            }
        }
    }
}

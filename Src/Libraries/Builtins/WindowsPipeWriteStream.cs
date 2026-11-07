/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
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
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace IronRuby.Builtins {
    /// <summary>
    /// The write end of an IO.pipe on Windows (WindowsSpawn.OsPipe). A blocking WriteFile on a
    /// full pipe waits until the reader takes the bytes, however long that is, so IO#timeout -
    /// which RubyIO applies to a stream that can time out as its WriteTimeout - could not bound
    /// it: a write into a full pipe hung for ever. With a timeout this writes no more than the
    /// pipe has room for at a time (IoReadiness.GetWindowsPipeRoom), waits for room in between,
    /// and gives up with a TimeoutException, which RubyBufferedStream turns into IO::TimeoutError.
    /// Without one it is the plain FileStream it was.
    ///
    /// A FileStream still, so what looks up the handle behind an IO (spawn redirections,
    /// IO.select via IoReadiness.GetWindowsPipe) finds it.
    /// </summary>
    internal sealed class WindowsPipeWriteStream : FileStream {
        private int _writeTimeout = Timeout.Infinite;
        private int _readTimeout = Timeout.Infinite;

        public WindowsPipeWriteStream(SafeFileHandle/*!*/ handle)
            : base(handle, FileAccess.Write, 1, false) {
        }

        public override bool CanTimeout {
            get { return true; }
        }

        public override int WriteTimeout {
            get { return _writeTimeout; }
            set { _writeTimeout = value; }
        }

        // RubyIO sets both timeouts on a stream that can time out; this end is never read.
        public override int ReadTimeout {
            get { return _readTimeout; }
            set { _readTimeout = value; }
        }

        // FileStream routes Write(ReadOnlySpan<byte>) and WriteByte of a derived class through
        // this overload, so it is the one place every synchronous write passes.
        public override void Write(byte[]/*!*/ buffer, int offset, int count) {
            int timeout = _writeTimeout;
            if (timeout == Timeout.Infinite || count == 0) {
                base.Write(buffer, offset, count);
                return;
            }

            long deadline = Environment.TickCount64 + timeout;
            while (count > 0) {
                int room = IoReadiness.GetWindowsPipeRoom(SafeFileHandle);
                if (room == 0) {
                    if (Environment.TickCount64 >= deadline) {
                        throw new TimeoutException();
                    }
                    Thread.Sleep(1);
                    continue;
                }
                int chunk = Math.Min(room, count);
                base.Write(buffer, offset, chunk);
                offset += chunk;
                count -= chunk;
            }
        }
    }
}

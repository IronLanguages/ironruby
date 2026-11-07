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
using System.Net.Sockets;
using System.IO;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;

namespace IronRuby.StandardLibrary.Sockets {
    internal class SocketStream : Stream, IDescriptorStream, INonBlockingWriteStream {
        internal readonly Socket/*!*/ _socket;

        internal Socket/*!*/ Socket {
            get { return _socket; }
        }

        public SocketStream(Socket/*!*/ s) {
            Assert.NotNull(s);
            _socket = s;
        }

        /// <summary>
        /// The socket's own descriptor. Without it IO.select had nothing to hand poll(2) and
        /// fell back to "no descriptor, so assume ready", which reported every socket as
        /// readable whether or not anything had arrived.
        /// </summary>
        public int Descriptor {
            get { return (int)_socket.Handle; }
        }

        public override bool CanRead {
            get { return true; }
        }

        public override bool CanSeek {
            get { return false; }
        }

        public override bool CanWrite {
            get { return true; }
        }

        public override void Close() {
            base.Close();
            _socket.Close();
        }

        public override void Flush() {
        }

        public override long Length {
            get {
                throw new InvalidOperationException();
            }
        }

        public override long Position {
            get {
                throw new InvalidOperationException();
            }
            set {
                throw new InvalidOperationException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) {
            return RubyBasicSocket.Blocking(_socket, SelectMode.SelectRead, () => _socket.Receive(buffer, offset, count, SocketFlags.None));
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new InvalidOperationException();
        }

        public override void SetLength(long value) {
            throw new InvalidOperationException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            RubyBasicSocket.Blocking<int>(() => _socket.Send(buffer, offset, count, SocketFlags.None));
        }

        // On a non-blocking socket Send takes what fits in the kernel buffer and says how much;
        // Write above drops that count, so #write_nonblock has to come here instead.
        public int WriteNonBlocking(byte[]/*!*/ buffer, int offset, int count) {
            return _socket.Send(buffer, offset, count, SocketFlags.None);
        }
    }
}

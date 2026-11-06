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
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Sockets {
    [RubyClass("TCPServer")]
    public class TCPServer : TCPSocket {
        private object _mutex = new object();
        private IAsyncResult _acceptResult;

        /// <summary>
        /// Creates an uninitialized socket.
        /// </summary>
        public TCPServer(RubyContext/*!*/ context)
            : base(context) {
        }

        public TCPServer(RubyContext/*!*/ context, Socket/*!*/ socket) 
            : base(context, socket) {
        }

        public override WaitHandle/*!*/ CreateReadWaitHandle() {
            return GetAcceptResult().AsyncWaitHandle;
        }

        private IAsyncResult/*!*/ GetAcceptResult() {
            if (_acceptResult == null) {
                lock (_mutex) {
                    if (_acceptResult == null) {
                        _acceptResult = Socket.BeginAccept(null, null);
                    }
                }
            }
            return _acceptResult;
        }

        private Socket/*!*/ Accept() {
            // acquire the result and replace it by null, so that no other thread can acquire it:
            IAsyncResult result = Interlocked.Exchange(ref _acceptResult, null);

            if (result == null) {
                return RubyBasicSocket.BlockingAccept(Socket, "TCPServer#accept", () => Socket.Accept());
            }

            // wait until accept finishes:
            return Socket.EndAccept(result);
        }

        [RubyConstructor]
        public static TCPServer/*!*/ CreateTCPServer(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            RubyClass/*!*/ self, [DefaultProtocol]MutableString hostname, [DefaultParameterValue(null)]object port) {
            return new TCPServer(self.Context, CreateSocket(stringCast, fixnumCast, hostname, port));
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static TCPServer/*!*/ Reinitialize(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast,
            TCPServer/*!*/ self, [DefaultProtocol]MutableString hostname, [DefaultParameterValue(null)]object port) {
            self.Socket = CreateSocket(stringCast, fixnumCast, hostname, port);
            return self;
        }

        private static Socket CreateSocket(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast,
            [DefaultProtocol]MutableString hostname, [DefaultParameterValue(null)]object port) {

            IPAddress listeningInterface = null;
            if (hostname == null) {
                listeningInterface = new IPAddress(0);
            } else if (hostname.IsEmpty) {
                listeningInterface = IPAddress.Any;
            } else {
                string hostnameStr = hostname.ConvertToString();
                if (hostnameStr == IPAddress.Any.ToString()) {
                    listeningInterface = IPAddress.Any;
                } else if (hostnameStr == IPAddress.Loopback.ToString()) {
                    listeningInterface = IPAddress.Loopback;
                } else if (!IPAddress.TryParse(hostnameStr, out listeningInterface)) {

                    // look up the host IP from DNS; an IPv6-only name is served over IPv6
                    // rather than rejected, which is what pinning AddressFamily.InterNetwork
                    // here used to do.
                    listeningInterface = GetHostAddress(hostnameStr);
                }
                Assert.NotNull(listeningInterface);
            }

            Socket socket = NewSocket(listeningInterface.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try {
                // CRuby's TCPServer sets SO_REUSEADDR before bind(2), but that cannot be done
                // through .NET: SocketOptionName.ReuseAddress is translated to SO_REUSEPORT on
                // Unix (Windows' SO_REUSEADDR means what Unix calls SO_REUSEPORT), and setting
                // *that* lets a second TCPServer bind a port another one is already listening
                // on -- so "raises Errno::EADDRINUSE when address is already in use" breaks.
                // Where libc is reachable, set the real SO_REUSEADDR (Linux: SOL_SOCKET 1,
                // SO_REUSEADDR 2); failing that is harmless, the bind just behaves as before.
                if (PosixMessages.HasSocketOptions) {
                    PosixMessages.SetSocketOption((int)socket.Handle, 1, 2, BitConverter.GetBytes(1));
                }
                socket.Bind(new IPEndPoint(listeningInterface, ConvertToPortNum(stringCast, fixnumCast, port)));
                socket.Listen(128);
            } catch (SocketException e) {
                socket.Close();
                throw SocketErrorOps.ToRubyException(e);
            }
            return socket;
        }

        [RubyMethod("accept")]
        public static TCPSocket/*!*/ Accept(RubyContext/*!*/ context, TCPServer/*!*/ self) {
            return new TCPSocket(context, self.Accept());
        }

        [RubyMethod("accept_nonblock")]
        public static TCPSocket/*!*/ AcceptNonBlocking(RubyContext/*!*/ context, TCPServer/*!*/ self) {
            bool blocking = self.Socket.Blocking;
            try {
                self.Socket.Blocking = false;
                return Accept(context, self);
            } finally {
                // Reset the blocking
                self.Socket.Blocking = blocking;
            }
        }

        [RubyMethod("sysaccept")]
        public static int SysAccept(RubyContext/*!*/ context, TCPServer/*!*/ self) {
            return Accept(context, self).GetFileDescriptor();
        }

        [RubyMethod("listen")]
        public static void Listen(TCPServer/*!*/ self, int backlog) {
            self.Socket.Listen(backlog);
        }
    }
}

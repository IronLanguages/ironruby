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
using IronRuby.Builtins;
using IronRuby.Runtime;
using System.Runtime.InteropServices;
using System.Net;

namespace IronRuby.StandardLibrary.Sockets {
    [RubyClass("TCPSocket")]
    public class TCPSocket : IPSocket {
        /// <summary>
        /// Creates an uninitialized socket.
        /// </summary>
        public TCPSocket(RubyContext/*!*/ context)
            : base(context) {
        }
        
        public TCPSocket(RubyContext/*!*/ context, Socket/*!*/ socket)
            : base(context, socket) {
        }

        [RubyConstructor]
        public static TCPSocket/*!*/ CreateTCPSocket(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            RubyClass/*!*/ self, [DefaultProtocol]MutableString remoteHost, object remotePort, [Optional]int localPort) {

            // Not sure what the semantics should be in this case but we make sure not to blow up.
            // Real-world code (Server.connect_to in memcache.rb in the memcache-client gem) does do "TCPSocket.new(host, port, 0)"
            if (localPort != 0) {
                throw new NotImplementedError();
            }

            return new TCPSocket(self.Context, CreateSocket(remoteHost, ConvertToPortNum(stringCast, fixnumCast, remotePort)));
        }

        [RubyConstructor]
        public static TCPSocket/*!*/ CreateTCPSocket(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            RubyClass/*!*/ self, 
            [DefaultProtocol]MutableString remoteHost, object remotePort,
            [DefaultProtocol]MutableString localHost, object localPort) {

            // The local endpoint has to be bound *before* connecting: bind(2) on a connected
            // socket is EINVAL, which is what doing this the other way round produced.
            return new TCPSocket(self.Context, CreateSocket(
                remoteHost, ConvertToPortNum(stringCast, fixnumCast, remotePort),
                localHost, ConvertToPortNum(stringCast, fixnumCast, localPort)
            ));
        }

        // Reinitialization. Not called when a factory/non-default ctor is called - but it is what
        // a Ruby subclass's .new runs (allocate, then #initialize). These took a TCPServer, so
        // `class MySocket < TCPSocket; end; MySocket.new(host, port)` bound to nothing but
        // IO#initialize and failed with "no implicit conversion of MySocket into TCPServer".
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static TCPSocket/*!*/ Reinitialize(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast,
            TCPSocket/*!*/ self, [DefaultProtocol]MutableString remoteHost, object remotePort, [Optional]int localPort) {

            // Not sure what the semantics should be in this case but we make sure not to blow up.
            // Real-world code (Server.connect_to in memcache.rb in the memcache-client gem) does do "TCPSocket.new(host, port, 0)"
            if (localPort != 0) {
                throw new NotImplementedError();
            }

            self.Socket = CreateSocket(remoteHost, ConvertToPortNum(stringCast, fixnumCast, remotePort));
            return self;
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static TCPSocket/*!*/ Reinitialize(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast,
            TCPSocket/*!*/ self,
            [DefaultProtocol]MutableString remoteHost, object remotePort,
            [DefaultProtocol]MutableString localHost, object localPort) {

            self.Socket = CreateSocket(
                remoteHost, ConvertToPortNum(stringCast, fixnumCast, remotePort),
                localHost, ConvertToPortNum(stringCast, fixnumCast, localPort));
            return self;
        }

        private static Socket/*!*/ CreateSocket(MutableString remoteHost, int port) {
            return CreateSocket(remoteHost, port, null, 0);
        }

        private static Socket/*!*/ CreateSocket(MutableString remoteHost, int port, MutableString localHost, int localPort) {
            // Resolve first: the address family has to follow the address, not be pinned to
            // InterNetwork, or TCPSocket.new("::1", port) can never work and every IPv6-guarded
            // spec in the tree silently skips.  A host can resolve to several addresses -- and a
            // nil host means the loopback of *either* family -- so connect(2) is tried against
            // each in turn, the way CRuby walks the getaddrinfo list, rather than giving up on
            // the first one.
            IPAddress[] addresses = GetHostAddresses(remoteHost != null ? remoteHost.ConvertToString() : null);

            Exception failure = null;
            foreach (IPAddress address in addresses) {
                Socket socket = null;
                // CRuby says which call failed, for the host and port as they were given.
                string call = "socket(2)";
                try {
                    // A host without IPv6 refuses even to create the socket (EAFNOSUPPORT); that is one
                    // more address that did not work, and the next one is tried.
                    socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    if (localHost != null || localPort != 0) {
                        IPAddress localAddress = localHost != null
                            ? GetHostAddress(localHost.ConvertToString(), address.AddressFamily)
                            : (address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any);
                        call = "bind(2)";
                        socket.Bind(new IPEndPoint(localAddress, localPort));
                    }
                    call = "connect(2)";
                    socket.Connect(address, port);
                    return socket;
                } catch (SocketException e) {
                    socket?.Close();
                    failure = call == "bind(2)"
                        ? SocketErrorOps.ToRubyException(e, DescribeEndPoint(call, localHost, localPort))
                        : SocketErrorOps.ToRubyException(e, DescribeEndPoint(call, remoteHost, port));
                }
            }
            throw failure;
        }

        [RubyMethod("gethostbyname", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetHostByName(ConversionStorage<MutableString>/*!*/ stringCast, RubyClass/*!*/ self, object hostNameOrAddress) {
            return GetHostByName(self.Context, ConvertToHostString(stringCast, hostNameOrAddress), false);
        }
    }
}

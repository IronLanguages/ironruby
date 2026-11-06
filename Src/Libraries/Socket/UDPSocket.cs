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


using System.Net;
using System.Net.Sockets;
using Microsoft.Scripting.Runtime;
using IronRuby.Builtins;
using IronRuby.Runtime;
using System.Runtime.InteropServices;

namespace IronRuby.StandardLibrary.Sockets {
    [RubyClass("UDPSocket")]
    public class UDPSocket : IPSocket {
        /// <summary>
        /// Creates an uninitialized socket.
        /// </summary>
        public UDPSocket(RubyContext/*!*/ context)
            : base(context) {
        }
        
        public UDPSocket(RubyContext/*!*/ context, Socket/*!*/ socket)
            : base(context, socket) {
        }

        [RubyConstructor]
        public static UDPSocket/*!*/ CreateUDPSocket(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            RubyClass/*!*/ self, [DefaultParameterValue(null)]object family) {
            return new UDPSocket(self.Context, CreateSocket(ConvertToAddressFamily(stringCast, fixnumCast, family)));
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static UDPSocket/*!*/ Reinitialize(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast,
            UDPSocket/*!*/ self, [DefaultParameterValue(null)]object family) {

            self.Socket = CreateSocket(ConvertToAddressFamily(stringCast, fixnumCast, family));
            return self;
        }

        private static Socket/*!*/ CreateSocket(AddressFamily addressFamily) {
            return RubyBasicSocket.NewSocket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
        }

#region Public Instance Methods

        [RubyMethod("bind")]
        public static int Bind(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            UDPSocket/*!*/ self, object hostNameOrAddress, object port) {
            int iPort = ConvertToPortNum(stringCast, fixnumCast, port);
            IPAddress address = (hostNameOrAddress != null) ? 
                GetHostAddress(ConvertToHostString(stringCast, hostNameOrAddress)) : IPAddress.Loopback;

            IPEndPoint ep = new IPEndPoint(address, iPort);
            self.Socket.Bind(ep);
            return 0;
        }

        [RubyMethod("connect")]
        public static int Connect(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            UDPSocket/*!*/ self, object hostname, object port) {

            int iPort = ConvertToPortNum(stringCast, fixnumCast, port);
            if (hostname == null) {
                // getaddrinfo(3) with a null node resolves to the loopback; ConvertToHostString
                // turned nil into an empty name the resolver answers with EAI_NONAME.
                IPAddress loopback = (self.Socket.AddressFamily == AddressFamily.InterNetworkV6)
                    ? IPAddress.IPv6Loopback : IPAddress.Loopback;
                EndPoint ep = new IPEndPoint(loopback, iPort);
                Blocking(() => self.Socket.Connect(ep));
                return 0;
            }
            // Resolved here rather than by Socket.Connect(string, int), so that a name that does not
            // resolve fails the way every other lookup does ("getaddrinfo: ...").
            IPAddress address = GetHostAddress(ConvertToHostString(stringCast, hostname), self.Socket.AddressFamily);
            Blocking(() => self.Socket.Connect(address, iPort));
            return 0;
        }

        [RubyMethod("recvfrom_nonblock")]
        public static RubyArray/*!*/ ReceiveFromNonBlocking(ConversionStorage<int>/*!*/ fixnumCast, IPSocket/*!*/ self, int length) {
            bool blocking = self.Socket.Blocking;
            try {
                self.Socket.Blocking = false;
                return ReceiveFrom(fixnumCast, self, length, null);
            } finally {
                // Reset the blocking
                self.Socket.Blocking = blocking;
            }
        }

        [RubyMethod("recvfrom_nonblock")]
        public static RubyArray/*!*/ ReceiveFromNonBlocking(ConversionStorage<int>/*!*/ fixnumCast, IPSocket/*!*/ self, int length, object/*Numeric*/ flags) {
            bool blocking = self.Socket.Blocking;
            try {
                self.Socket.Blocking = false;
                return ReceiveFrom(fixnumCast, self, length, flags);
            } finally {
                // Reset the blocking
                self.Socket.Blocking = blocking;
            }
        }

        [RubyMethod("send")]
        public static int Send(ConversionStorage<int>/*!*/ fixnumCast, ConversionStorage<MutableString>/*!*/ stringCast,
            RubyBasicSocket/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ message,
            object flags, object hostNameOrAddress, object port) {

            int iPort = ConvertToPortNum(stringCast, fixnumCast, port);
            SocketFlags sFlags = ConvertToSocketFlag(fixnumCast, flags);

            // Convert the parameters
            IPAddress address = (hostNameOrAddress != null) ?
                GetHostAddress(ConvertToHostString(stringCast, hostNameOrAddress)) : IPAddress.Loopback;

            EndPoint toEndPoint = new IPEndPoint(address, iPort);
            return Blocking(() => self.Socket.SendTo(message.ConvertToBytes(), sFlags, toEndPoint));
        }

        // These overwritten methods have to be here because we kill the ones in RubyBasicSocket by creating the one above
        [RubyMethod("send")]
        public static new int Send(ConversionStorage<int>/*!*/ fixnumCast, 
            RubyBasicSocket/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ message, object flags) {
            return RubyBasicSocket.Send(fixnumCast, self, message, flags);
        }

        [RubyMethod("send")]
        public static new int Send(ConversionStorage<int>/*!*/ fixnumCast,
            RubyBasicSocket/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ message, object flags, 
            [DefaultProtocol, NotNull]MutableString/*!*/ to) {

            return RubyBasicSocket.Send(fixnumCast, self, message, flags, to);
        }

        #endregion
    }
}

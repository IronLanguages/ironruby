/* ****************************************************************************
 *
 * AF_UNIX sockets.
 *
 * IronRuby never had UNIXSocket or UNIXServer at all -- `require "socket"` left
 * both constants undefined, which is what the whole of spec/library/socket's
 * unixsocket and unixserver trees tripped over before anything else could be
 * measured.  .NET has had UnixDomainSocketEndPoint since Core 2.1, so the
 * connect/bind/accept half of this is ordinary System.Net.Sockets work; the two
 * pieces it has no answer for -- socketpair(2) and descriptor passing over
 * SCM_RIGHTS -- come from libc, in the same style as Src/Libraries/Builtins/Posix.cs.
 *
 * ***************************************************************************/


using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Scripting.Runtime;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Sockets {

    [RubyClass("UNIXSocket")]
    public class UNIXSocket : RubyBasicSocket {

        /// <summary>
        /// Creates an uninitialized socket.
        /// </summary>
        public UNIXSocket(RubyContext/*!*/ context)
            : base(context) {
        }

        public UNIXSocket(RubyContext/*!*/ context, Socket/*!*/ socket)
            : base(context, socket) {
        }

        #region Construction

        [RubyConstructor]
        public static UNIXSocket/*!*/ CreateUNIXSocket(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            return new UNIXSocket(self.Context, Connect(self.Context, path));
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static UNIXSocket/*!*/ Reinitialize(UNIXSocket/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            self.Socket = Connect(self.Context, path);
            return self;
        }

        private static Socket/*!*/ Connect(RubyContext/*!*/ context, MutableString/*!*/ path) {
            // an embedded NUL would silently truncate the name (CVE-2018-8779)
            string pathStr = context.DecodePath(Protocols.CheckPath(path));
            Socket socket = NewUnixSocket(SocketType.Stream);
            try {
                socket.Connect(ToEndPoint(pathStr));
            } catch (Exception e) {
                socket.Close();
                throw ToConnectException(e, pathStr);
            }
            return socket;
        }

        /// <summary>
        /// .NET collapses the connect(2) errno for a path that does not exist into
        /// AddressNotAvailable, which would surface as a bare SocketError.  EADDRNOTAVAIL is not
        /// something an AF_UNIX connect can actually report, so the only thing it can mean here is
        /// the ENOENT that CRuby raises.
        /// </summary>
        internal static Exception/*!*/ ToConnectException(Exception/*!*/ e, string/*!*/ path) {
            SocketException se = e as SocketException;
            if (se != null && se.SocketErrorCode == SocketError.AddressNotAvailable) {
                return RubyExceptions.CreateENOENT("connect(2) for {0}", path);
            }
            return SocketErrorOps.ToRubyException(e, "connect(2) for " + path);
        }

        internal static Socket/*!*/ NewUnixSocket(SocketType type) {
            return new Socket(AddressFamily.Unix, type, ProtocolType.Unspecified);
        }

        /// <summary>
        /// UnixDomainSocketEndPoint rejects an empty path outright, so a placeholder is needed
        /// wherever an endpoint has to exist before it is filled in (ReceiveFrom's `ref` argument).
        /// </summary>
        internal static EndPoint/*!*/ ToEndPoint(string/*!*/ path) {
            return new UnixDomainSocketEndPoint(path.Length == 0 ? "/" : path);
        }

        /// <summary>
        /// The endpoint to bind(2) to. A socket bound through a UnixDomainSocketEndPoint deletes
        /// its file when .NET disposes it; CRuby's leaves the file where it is - closing a
        /// UNIXServer does not unlink its path, and code that keeps a socket file across
        /// restarts (or, like puma, must not remove a path it did not create) relies on that.
        /// Binding through this wrapper, which .NET does not recognise, keeps the file.
        /// </summary>
        internal static EndPoint/*!*/ ToBindEndPoint(string/*!*/ path) {
            return new KeepFileEndPoint((UnixDomainSocketEndPoint)ToEndPoint(path));
        }

        private sealed class KeepFileEndPoint : EndPoint {
            private readonly UnixDomainSocketEndPoint/*!*/ _endPoint;

            public KeepFileEndPoint(UnixDomainSocketEndPoint/*!*/ endPoint) {
                _endPoint = endPoint;
            }

            public override AddressFamily AddressFamily {
                get { return AddressFamily.Unix; }
            }

            public override SocketAddress/*!*/ Serialize() {
                return _endPoint.Serialize();
            }

            public override EndPoint/*!*/ Create(SocketAddress/*!*/ socketAddress) {
                return _endPoint.Create(socketAddress);
            }

            public override string/*!*/ ToString() {
                return _endPoint.ToString();
            }
        }

        #endregion

        #region Addresses

        /// <summary>
        /// The path an endpoint names.  UnixDomainSocketEndPoint.ToString() answers "" for an
        /// unbound or anonymous socket -- exactly what CRuby's UNIXSocket#path reports for the
        /// client side of a connection and for both ends of a socketpair.
        /// </summary>
        internal static string/*!*/ PathOf(EndPoint endPoint) {
            return endPoint == null ? "" : endPoint.ToString();
        }

        internal static MutableString/*!*/ EncodePath(RubyContext/*!*/ context, string/*!*/ path) {
            return path.Length == 0 ? MutableString.CreateEmpty() : context.EncodePath(path);
        }

        internal static RubyArray/*!*/ AddressArray(RubyContext/*!*/ context, EndPoint endPoint) {
            RubyArray result = new RubyArray(2);
            result.Add(MutableString.CreateAscii("AF_UNIX"));
            result.Add(EncodePath(context, PathOf(endPoint)));
            return result;
        }

        [RubyMethod("path")]
        public static MutableString/*!*/ GetPath(RubyContext/*!*/ context, UNIXSocket/*!*/ self) {
            return EncodePath(context, PathOf(self.Socket.LocalEndPoint));
        }

        [RubyMethod("addr")]
        public static RubyArray/*!*/ GetLocalAddress(RubyContext/*!*/ context, UNIXSocket/*!*/ self) {
            return AddressArray(context, self.Socket.LocalEndPoint);
        }

        [RubyMethod("peeraddr")]
        public static RubyArray/*!*/ GetPeerAddress(RubyContext/*!*/ context, UNIXSocket/*!*/ self) {
            EndPoint remote;
            try {
                remote = self.Socket.RemoteEndPoint;
            } catch (SocketException e) {
                throw SocketErrorOps.ToRubyException(e);
            }
            if (remote == null) {
                throw new Errno.NotConnectedError();
            }
            return AddressArray(context, remote);
        }

        /// <summary>
        /// recvfrom(2).  On a connected stream socket the kernel fills in no source address at all
        /// -- .NET leaves the `ref` endpoint untouched -- and CRuby reports the peer, so take it
        /// from getpeername(2).  A datagram socket does get a real source address.
        /// </summary>
        [RubyMethod("recvfrom")]
        public static RubyArray/*!*/ ReceiveFrom(ConversionStorage<int>/*!*/ fixnumCast, UNIXSocket/*!*/ self, int length) {
            return ReceiveFrom(fixnumCast, self, length, null);
        }

        [RubyMethod("recvfrom")]
        public static RubyArray/*!*/ ReceiveFrom(ConversionStorage<int>/*!*/ fixnumCast, UNIXSocket/*!*/ self,
            int length, object/*Numeric*/ flags) {

            SocketFlags socketFlags = ConvertToSocketFlag(fixnumCast, flags);
            byte[] buffer = new byte[length];
            Socket socket = self.Socket;

            EndPoint from;
            int received;
            if (socket.SocketType == SocketType.Stream) {
                received = Blocking(socket, SelectMode.SelectRead, () => socket.Receive(buffer, socketFlags));
                from = socket.Connected ? socket.RemoteEndPoint : null;
            } else {
                EndPoint sender = ToEndPoint("");
                received = Blocking(socket, SelectMode.SelectRead, () => socket.ReceiveFrom(buffer, socketFlags, ref sender));
                from = sender;
            }

            MutableString str = MutableString.CreateBinary();
            str.Append(buffer, 0, received);
            return RubyOps.MakeArray2(str, AddressArray(self.Context, from));
        }

        #endregion

        #region send_io, recv_io

        /// <summary>
        /// IO#fileno is an index into RubyContext's own descriptor table, not an operating system
        /// descriptor, so neither end of a descriptor pass can go through it: what travels over
        /// SCM_RIGHTS has to be the descriptor the kernel knows, and what comes back has to be
        /// given a table entry of its own before Ruby can name it.
        /// </summary>
        [RubyMethod("__ir_raw_send_io")]
        public static void SendDescriptor(UNIXSocket/*!*/ self, [NotNull]RubyIO/*!*/ io) {
            int descriptor = io.KernelDescriptor;
            if (descriptor < 0) {
                throw RubyExceptions.CreateIOError("cannot send an IO that has no file descriptor");
            }
            io.Flush();
            int errno;
            if (PosixMessages.SendDescriptor((int)self.Socket.Handle, descriptor, out errno) < 0) {
                throw Posix.Error(errno, null);
            }
        }

        [RubyMethod("__ir_raw_recv_io")]
        public static int ReceiveDescriptor(RubyContext/*!*/ context, UNIXSocket/*!*/ self) {
            Socket socket = self.Socket;
            int errno = 0;
            int descriptor = Blocking(socket, SelectMode.SelectRead,
                () => PosixMessages.ReceiveDescriptor((int)socket.Handle, out errno));
            if (errno != 0) {
                throw Posix.Error(errno, null);
            }
            if (descriptor < 0) {
                // The peer sent data but no descriptor, which is what CRuby reports this way.
                throw RubyExceptions.CreateIOError("file descriptor was not passed");
            }

            IOMode mode;
            if (!RubyIO.TryGetDescriptorMode(descriptor, out mode)) {
                mode = IOMode.ReadWrite;
            }
            IOMode access = mode & IOMode.ReadWriteMask;
            return context.AllocateFileDescriptor(
                new DescriptorStream(descriptor, access != IOMode.WriteOnly, access != IOMode.ReadOnly, true));
        }

        #endregion
    }

    [RubyClass("UNIXServer")]
    public class UNIXServer : UNIXSocket {

        public UNIXServer(RubyContext/*!*/ context)
            : base(context) {
        }

        public UNIXServer(RubyContext/*!*/ context, Socket/*!*/ socket)
            : base(context, socket) {
        }

        [RubyConstructor]
        public static UNIXServer/*!*/ CreateUNIXServer(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            return new UNIXServer(self.Context, Bind(self.Context, path));
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static UNIXServer/*!*/ Reinitialize(UNIXServer/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            self.Socket = Bind(self.Context, path);
            return self;
        }

        private static Socket/*!*/ Bind(RubyContext/*!*/ context, MutableString/*!*/ path) {
            // an embedded NUL would silently truncate the name (CVE-2018-8779)
            string pathStr = context.DecodePath(Protocols.CheckPath(path));
            Socket socket = NewUnixSocket(SocketType.Stream);
            try {
                socket.Bind(ToBindEndPoint(pathStr));
                socket.Listen(128);
            } catch (Exception e) {
                socket.Close();
                // MRI's init_unixsock says "connect(2)" for the server's bind(2) too.
                throw SocketErrorOps.ToRubyException(e, "connect(2) for " + pathStr);
            }
            return socket;
        }

        private Socket/*!*/ Accept() {
            return BlockingAccept(Socket, "UNIXServer#accept", () => Socket.Accept());
        }

        [RubyMethod("accept")]
        public static UNIXSocket/*!*/ Accept(RubyContext/*!*/ context, UNIXServer/*!*/ self) {
            return new UNIXSocket(context, self.Accept());
        }

        [RubyMethod("accept_nonblock")]
        public static UNIXSocket/*!*/ AcceptNonBlocking(RubyContext/*!*/ context, UNIXServer/*!*/ self) {
            bool blocking = self.Socket.Blocking;
            try {
                self.Socket.Blocking = false;
                return Accept(context, self);
            } finally {
                self.Socket.Blocking = blocking;
            }
        }

        [RubyMethod("sysaccept")]
        public static int SysAccept(RubyContext/*!*/ context, UNIXServer/*!*/ self) {
            return Accept(context, self).GetFileDescriptor();
        }

        [RubyMethod("listen")]
        public static void Listen(UNIXServer/*!*/ self, [DefaultProtocol]int backlog) {
            self.Socket.Listen(backlog);
        }
    }

}


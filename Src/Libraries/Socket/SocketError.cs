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
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using IronRuby.Builtins;

namespace IronRuby.StandardLibrary.Sockets {
    [RubyClass("SocketError", Extends = typeof(SocketException), Inherits = typeof(SystemException))]
    [HideMethod("message")] // SocketException overrides Message so we have to hide it here
    public static class SocketErrorOps {
        [RubyConstructor]
        public static Exception/*!*/ Create(RubyClass/*!*/ self, [DefaultParameterValue(null)]object message) {
            return RubyExceptionData.InitializeException(new SocketException(0), message ?? MutableString.CreateAscii("SocketError"));

        }

        public static Exception/*!*/ Create(MutableString/*!*/ message) {
            return RubyExceptionData.InitializeException(new SocketException(0), message);
        }

        /// <summary>
        /// .NET reports every socket failure as a SocketException, and because Socket::SocketError
        /// *is* that CLR type an unhandled one surfaces in Ruby as SocketError.  CRuby raises an
        /// Errno for anything the kernel reported through errno and keeps SocketError for name
        /// resolution, so translate the codes that have an Errno class here.  The rest are mapped
        /// in Src/StdLib/ironruby/socket.rb, where the whole Errno table is available.
        /// </summary>
        internal static Exception/*!*/ ToRubyException(Exception/*!*/ e) {
            return ToRubyException(e, null);
        }

        /// <param name="detail">What failed, as CRuby words it after the strerror text:
        /// <c>connect(2) for "localhost" port 80</c>.</param>
        internal static Exception/*!*/ ToRubyException(Exception/*!*/ e, string detail) {
            SocketException se = e as SocketException;
            if (se != null) {
                switch (se.SocketErrorCode) {
                    case System.Net.Sockets.SocketError.ConnectionRefused:
                        return new Errno.ConnectionRefusedError(detail);
                    case System.Net.Sockets.SocketError.AddressAlreadyInUse:
                        return new Errno.AddressInUseError(detail);
                    case System.Net.Sockets.SocketError.ConnectionReset:
                        return new Errno.ConnectionResetError(detail);
                    case System.Net.Sockets.SocketError.ConnectionAborted:
                        return new Errno.ConnectionAbortedError(detail);
                    case System.Net.Sockets.SocketError.NotConnected:
                        return new Errno.NotConnectedError(detail);
                    case System.Net.Sockets.SocketError.HostDown:
                        return new Errno.HostDownError(detail);
                    case System.Net.Sockets.SocketError.Shutdown:
                        return new Errno.PipeError(detail);
                    case System.Net.Sockets.SocketError.InvalidArgument:
                        return new InvalidError(detail);
                }
                return e;
            }
            if (e is ObjectDisposedException) {
                return RubyExceptions.CreateIOError("closed stream");
            }
            return e;
        }
    }
}


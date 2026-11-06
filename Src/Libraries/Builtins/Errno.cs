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
using System.Runtime.InteropServices;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using System.IO;

namespace IronRuby.Builtins {
    [RubyModule("Errno")]
    public static class Errno {
        /// <summary>
        /// [name, errno, strerror] for NOERROR and every name MRI's Errno module has, with this platform's
        /// numbers and texts (nil, nil where the platform lacks the error). The prelude
        /// (Src/StdLib/ironruby/ruby4.rb) makes the classes from it.
        /// </summary>
        [RubyMethod("__ir_errno_table__", RubyMethodAttributes.PrivateSingleton)]
        public static RubyArray/*!*/ GetErrnoTable(RubyModule/*!*/ self) {
            var result = new RubyArray();
            result.Add(new RubyArray(new object[] { MutableString.CreateAscii("NOERROR"), 0, MutableString.Create(RubyErrno.GetMessage(0), RubyEncoding.UTF8) }));
            foreach (string name in RubyErrno.Names) {
                int errno = RubyErrno.GetNumber(name);
                result.Add(new RubyArray(new object[] {
                    MutableString.CreateAscii(name),
                    errno != 0 ? (object)errno : null,
                    errno != 0 ? MutableString.Create(RubyErrno.GetMessage(errno), RubyEncoding.UTF8) : null,
                }));
            }
            return result;
        }

        internal static UnauthorizedAccessException/*!*/ CreateEACCES() {
            return new UnauthorizedAccessException();
        }

        internal static UnauthorizedAccessException/*!*/ CreateEACCES(string message) {
            return new UnauthorizedAccessException(message);
        }

        internal static UnauthorizedAccessException/*!*/ CreateEACCES(string message, Exception inner) {
            return new UnauthorizedAccessException(message, inner);
        }

        [RubyClass("EAGAIN")]
        public class ResourceTemporarilyUnavailableError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EAGAIN");

            public ResourceTemporarilyUnavailableError() : this(null, null) { }
            public ResourceTemporarilyUnavailableError(string message) : this(message, null) { }
            public ResourceTemporarilyUnavailableError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public ResourceTemporarilyUnavailableError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("EINTR")]
        public class InterruptedError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EINTR");

            public InterruptedError() : this(null, null) { }
            public InterruptedError(string message) : this(message, null) { }
            public InterruptedError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public InterruptedError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("EDOM")]
        public class DomainError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EDOM");

            public DomainError() : this(null, null) { }
            public DomainError(string message) : this(message, null) { }
            public DomainError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public DomainError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("EINVAL", Extends = typeof(InvalidError), Inherits = typeof(ExternalException))]
        public class InvalidErrorOps {
            [RubyConstructor]
            public static InvalidError/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                InvalidError result = new InvalidError(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("EINVAL")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("ENOENT", Extends = typeof(FileNotFoundException), Inherits = typeof(ExternalException))]
        public class FileNotFoundExceptionOps {
            [RubyConstructor]
            public static FileNotFoundException/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                FileNotFoundException result = new FileNotFoundException(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("ENOENT")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("ENOTDIR", Extends = typeof(DirectoryNotFoundException), Inherits = typeof(ExternalException))]
        public class DirectoryNotFoundExceptionOps {
            [RubyConstructor]
            public static DirectoryNotFoundException/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                DirectoryNotFoundException result = new DirectoryNotFoundException(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("ENOTDIR")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }
        [RubyClass("EACCES", Extends=typeof(System.UnauthorizedAccessException), Inherits=typeof(ExternalException))]
        public class UnauthorizedAccessExceptionOps {
            [RubyConstructor]
            public static UnauthorizedAccessException/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                UnauthorizedAccessException result = new UnauthorizedAccessException(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("EACCES")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("ECHILD")]
        public class ChildError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ECHILD");

            public ChildError() : this(null, null) { }
            public ChildError(string message) : this(message, null) { }
            public ChildError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public ChildError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("EISDIR", Extends = typeof(DirectoryIsError), Inherits = typeof(ExternalException))]
        public class DirectoryIsErrorOps {
            [RubyConstructor]
            public static DirectoryIsError/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                var result = new DirectoryIsError(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("EISDIR")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("EEXIST", Extends=typeof(ExistError), Inherits=typeof(ExternalException))]
        public class ExistErrorOps {
            [RubyConstructor]
            public static ExistError/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                var result = new ExistError(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("EEXIST")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("ENXIO", Extends = typeof(NoSuchDeviceOrAddressError), Inherits = typeof(ExternalException))]
        public class NoSuchDeviceOrAddressErrorOps {
            [RubyConstructor]
            public static NoSuchDeviceOrAddressError/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                var result = new NoSuchDeviceOrAddressError(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("ENXIO")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("EIO", Extends = typeof(InputOutputError), Inherits = typeof(ExternalException))]
        public class InputOutputErrorOps {
            [RubyConstructor]
            public static InputOutputError/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                var result = new InputOutputError(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("EIO")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("EBADF", Extends = typeof(BadFileDescriptorError), Inherits = typeof(ExternalException))]
        public class BadFileDescriptorErrorOps {
            [RubyConstructor]
            public static BadFileDescriptorError/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                var result = new BadFileDescriptorError(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("EBADF")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("ENOEXEC", Extends = typeof(ExecFormatError), Inherits = typeof(ExternalException))]
        public class ExecFormatErrorOps {
            [RubyConstructor]
            public static ExecFormatError/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                var result = new ExecFormatError(RubyExceptions.MakeMessage(ref message, RubyErrno.GetMessage("ENOEXEC")));
                RubyExceptionData.InitializeException(result, message);
                return result;
            }
        }

        [RubyClass("EPERM")]
        public class OperationNotPermittedError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EPERM");

            public OperationNotPermittedError() : this(null, null) { }
            public OperationNotPermittedError(string message) : this(message, null) { }
            public OperationNotPermittedError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public OperationNotPermittedError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("ELOOP")]
        public class TooManySymbolicLinksError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ELOOP");

            public TooManySymbolicLinksError() : this(null, null) { }
            public TooManySymbolicLinksError(string message) : this(message, null) { }
            public TooManySymbolicLinksError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public TooManySymbolicLinksError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("ENOTEMPTY")]
        public class DirectoryNotEmptyError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ENOTEMPTY");

            public DirectoryNotEmptyError() : this(null, null) { }
            public DirectoryNotEmptyError(string message) : this(message, null) { }
            public DirectoryNotEmptyError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public DirectoryNotEmptyError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("ENAMETOOLONG")]
        public class NameTooLongError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ENAMETOOLONG");

            public NameTooLongError() : this(null, null) { }
            public NameTooLongError(string message) : this(message, null) { }
            public NameTooLongError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public NameTooLongError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("EPIPE")]
        public class PipeError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EPIPE");

            public PipeError() : this(null, null) { }
            public PipeError(string message) : this(message, null) { }
            public PipeError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public PipeError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }


        [RubyClass("EXDEV")]
        public class ImproperLinkError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EXDEV");

            public ImproperLinkError() : this(null, null) { }
            public ImproperLinkError(string message) : this(message, null) { }
            public ImproperLinkError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public ImproperLinkError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("ESPIPE")]
        public class InvalidSeekError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ESPIPE");

            public InvalidSeekError() : this(null, null) { }
            public InvalidSeekError(string message) : this(message, null) { }
            public InvalidSeekError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public InvalidSeekError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        #region Socket Errors

        [RubyClass("EWOULDBLOCK")]
        public class WouldBlockError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EWOULDBLOCK");

            public WouldBlockError() : this(null, null) { }
            public WouldBlockError(string message) : this(message, null) { }
            public WouldBlockError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public WouldBlockError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("EADDRINUSE")]
        public class AddressInUseError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EADDRINUSE");

            public AddressInUseError() : this(null, null) { }
            public AddressInUseError(string message) : this(message, null) { }
            public AddressInUseError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public AddressInUseError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("ECONNABORTED")]
        public class ConnectionAbortedError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ECONNABORTED");

            public ConnectionAbortedError() : this(null, null) { }
            public ConnectionAbortedError(string message) : this(message, null) { }
            public ConnectionAbortedError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public ConnectionAbortedError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("ECONNRESET")]
        public class ConnectionResetError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ECONNRESET");

            public ConnectionResetError() : this(null, null) { }
            public ConnectionResetError(string message) : this(message, null) { }
            public ConnectionResetError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public ConnectionResetError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("ENOTCONN")]
        public class NotConnectedError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ENOTCONN");

            public NotConnectedError() : this(null, null) { }
            public NotConnectedError(string message) : this(message, null) { }
            public NotConnectedError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public NotConnectedError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyClass("ECONNREFUSED")]
        public class ConnectionRefusedError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("ECONNREFUSED");

            public ConnectionRefusedError() : this(null, null) { }
            public ConnectionRefusedError(string message) : this(message, null) { }
            public ConnectionRefusedError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public ConnectionRefusedError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }
        
        [RubyClass("EHOSTDOWN")]
        public class HostDownError : ExternalException {
            private static readonly string/*!*/ M = RubyErrno.GetMessage("EHOSTDOWN");

            public HostDownError() : this(null, null) { }
            public HostDownError(string message) : this(message, null) { }
            public HostDownError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public HostDownError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
        }

        #endregion
    }
}

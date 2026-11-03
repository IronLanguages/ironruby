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
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Remoting;
using System.Security;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    // The CLR's own allocation failure is Ruby's NoMemoryError, so an array or string that
    // cannot be allocated raises something `rescue NoMemoryError` sees, rather than a
    // System::OutOfMemoryException that no Ruby code expects.
    [RubyException("NoMemoryError", Extends = typeof(OutOfMemoryException), Inherits = typeof(Exception))]
    public static class NoMemoryErrorOps {
    }

    [RubyException("EOFError")]
    public class EOFError : IOException {
        public EOFError() : this(null, null) { }
        public EOFError(string message): this(message, null) { }
        public EOFError(string message, Exception inner) : base(message ?? "EOFError", inner) { }
    }

    [RubyException("FloatDomainError")]
    public class FloatDomainError : ArgumentOutOfRangeException {
        public FloatDomainError() : this(null, null) { }
        public FloatDomainError(string message) : this(message, null) { }
        public FloatDomainError(string message, Exception inner) : base(message ?? "FloatDomainError", inner) { }
    }

    [RubyException("ThreadError")]
    public class ThreadError : SystemException {
        public ThreadError() : this(null, null) { }
        public ThreadError(string message): this(message, null) { }
        public ThreadError(string message, Exception inner) : base(message ?? "ThreadError", inner) { }
    }

    [RubyException("StopIteration")]
    public class StopIteration : IndexError {
        public StopIteration() : this(null, null) { }
        public StopIteration(string message): this(message, null) { }
        public StopIteration(string message, Exception inner) : base(message ?? "StopIteration", inner) { }
    }

    [RubyException("ClosedQueueError")]
    public class ClosedQueueError : StopIteration {
        public ClosedQueueError() : this(null, null) { }
        public ClosedQueueError(string message): this(message, null) { }
        public ClosedQueueError(string message, Exception inner) : base(message ?? "ClosedQueueError", inner) { }
    }

    [RubyException("SystemExit", Extends = typeof(SystemExit))]
    public class SystemExitOps : Exception {
        [RubyMethod("status")]
        public static int GetStatus(SystemExit/*!*/ self) {
            return self.Status;
        }

        [RubyMethod("success?")]
        public static bool IsSuccessful(SystemExit/*!*/ self) {
            return self.Status == 0;
        }

        /// <summary>
        /// An exit status is an Integer, or one of the two Kernel#exit also takes: true for a
        /// successful exit and false for a failed one.
        /// </summary>
        private static bool TryGetStatus(object value, out int status) {
            if (value is int) {
                status = (int)value;
                return true;
            }
            if (value is bool) {
                status = (bool)value ? 0 : 1;
                return true;
            }
            status = 0;
            return false;
        }

        [RubyConstructor]
        public static SystemExit/*!*/ Factory(RubyClass/*!*/ self) {
            return Make(self, 0, null);
        }

        /// <summary>
        /// A single argument is the status when it looks like one and the message otherwise, so
        /// both `SystemExit.new(42)' and `SystemExit.new("bye")' say what they look like.
        /// </summary>
        [RubyConstructor]
        public static SystemExit/*!*/ Factory(RubyClass/*!*/ self, object statusOrMessage) {
            int status;
            return TryGetStatus(statusOrMessage, out status)
                ? Make(self, status, null)
                : Make(self, 0, statusOrMessage);
        }

        [RubyConstructor]
        public static SystemExit/*!*/ Factory(RubyClass/*!*/ self, object status, object message) {
            int value;
            if (!TryGetStatus(status, out value)) {
                // MRI only has room for a message after a status, so anything else in front of
                // one leaves it with an argument it cannot place.
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 2, expected 0..1)");
            }
            return Make(self, value, message);
        }

        /// <summary>
        /// A subclass of SystemExit is built by #allocate and #initialize rather than through the
        /// constructor above, so without this one `class C &lt; SystemExit; end; C.new(8)' came
        /// back with a status of zero and the program exited successfully.
        /// </summary>
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static SystemExit/*!*/ Initialize(SystemExit/*!*/ self, [NotNull]params object[]/*!*/ args) {
            int status = 0;
            object message = null;

            if (args.Length == 1) {
                if (!TryGetStatus(args[0], out status)) {
                    message = args[0];
                }
            } else if (args.Length >= 2) {
                if (args.Length > 2 || !TryGetStatus(args[0], out status)) {
                    throw RubyExceptions.CreateArgumentError(
                        "wrong number of arguments (given {0}, expected 0..1)", args.Length);
                }
                message = args[1];
            }

            self.Status = status;
            RubyExceptionData.InitializeException(self, message);
            return self;
        }

        private static SystemExit/*!*/ Make(RubyClass/*!*/ self, int status, object message) {
            SystemExit result = new SystemExit(status, RubyExceptionData.GetClrMessage(self, message ?? "SystemExit"));
            RubyExceptionData.InitializeException(result, message);
            return result;
        }
    }

    [RubyException("SignalException")]
    public class SignalException : Exception {
        public SignalException() : this(null, null) { }
        public SignalException(string message): this(message, null) { }
        public SignalException(string message, Exception inner) : base(message ?? "SignalException", inner) { }
    }

    [RubyException("Interrupt", Inherits = typeof(SignalException))]
    public class Interrupt : Exception { 
        public Interrupt() : this(null, null) { }
        public Interrupt(string message): this(message, null) { }
        public Interrupt(string message, Exception inner) : base(message ?? "Interrupt", inner) { }
    }

    [RubyException("LocalJumpError", Extends = typeof(LocalJumpError))]
    public static class LocalJumpErrorOps {
        [RubyMethod("reason")]
        public static RubySymbol/*!*/ GetReason(RubyContext/*!*/ context, LocalJumpError/*!*/ self) {
            return context.EncodeIdentifier(self.Reason ?? "noreason");
        }

        [RubyMethod("exit_value")]
        public static object GetExitValue(LocalJumpError/*!*/ self) {
            return self.ExitValue;
        }
    }

    [RubyException("ScriptError", Extends = typeof(ScriptError))]
    public static class ScriptErrorOps {
    }

    [RubyException("NotImplementedError", Extends = typeof(NotImplementedError))]
    public static class NotImplementedErrorOps {
    }

    [RubyException("LoadError", Extends = typeof(LoadError))]
    public static class LoadErrorOps {
    }

    [RubyException("RegexpError", Extends = typeof(RegexpError))]
    public static class RegexpErrorOps {
    }

    [RubyException("SyntaxError", Extends = typeof(SyntaxError))]
    public static class SyntaxErrorOps {
        /// <summary>
        /// The file the unparsable source came from - what `eval' was told to call it, or the
        /// file `require' was reading. Nil for a SyntaxError built by hand, which carries no
        /// source at all.
        /// </summary>
        [RubyMethod("path")]
        public static MutableString GetPath(RubyContext/*!*/ context, SyntaxError/*!*/ self) {
            return self.File == null ? null : context.EncodePath(self.File);
        }
    }

    [RubyException("SystemStackError", Extends = typeof(SystemStackError), Inherits = typeof(Exception))]
    public static class SystemStackErrorOps {
    }

    [RubyException("StandardError", Extends = typeof(SystemException), Inherits = typeof(Exception))]
    public static class SystemExceptionOps {
    }

    [RubyException("ArgumentError", Extends = typeof(ArgumentException), Inherits = typeof(SystemException))]
    [HideMethod("message")]
    public static class ArgumentErrorOps {
    }

    [RubyException("IOError", Extends = typeof(IOException), Inherits = typeof(SystemException))]
    public static class IOErrorOps {
    }

    [RubyException("IndexError", Extends = typeof(IndexError), Inherits = typeof(SystemException))]
    public static class IndexErrorOps {
    }

    [RubyException("RangeError", Extends = typeof(ArgumentOutOfRangeException), Inherits = typeof(SystemException))]
    [HideMethod("message")]
    public static class RangeErrorOps {
    }

    [RubyException("NameError", Extends = typeof(MemberAccessException), Inherits = typeof(SystemException))]
    public static class NameErrorOps {
        /// <summary>
        /// NameError.new([message [, name]] [, receiver: obj]). IronRuby has no real keyword
        /// arguments, so `receiver:` is recognised by shape: a trailing Hash whose only key is
        /// :receiver. NoMethodError inherits this parsing through NoMethodErrorOps.Factory.
        /// </summary>
        [RubyConstructor]
        public static MemberAccessException/*!*/ Factory(RubyClass/*!*/ self, params object[]/*!*/ args) {
            object receiver;
            bool hasReceiver = TryTakeReceiverKeyword(ref args, out receiver);
            if (args.Length > 2) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..2)", args.Length);
            }

            object message = args.Length > 0 ? args[0] : null;
            var result = new MemberAccessException(RubyExceptionData.GetClrMessage(self, message));
            RubyExceptionData.InitializeException(result, message);

            var data = RubyExceptionData.GetInstance(result);
            data.Name = args.Length > 1 ? args[1] : null;
            if (hasReceiver) {
                data.SetReceiver(receiver);
            }
            return result;
        }

        [RubyMethod("name")]
        public static object GetName(Exception/*!*/ self) {
            return RubyExceptionData.GetInstance(self).Name;
        }

        [RubyMethod("receiver")]
        public static object GetReceiver(Exception/*!*/ self) {
            var data = RubyExceptionData.GetInstance(self);
            if (!data.HasReceiver) {
                throw RubyExceptions.CreateArgumentError("no receiver is available");
            }
            return data.Receiver;
        }

        /// <summary>
        /// Splits a trailing `receiver:` keyword hash off the argument list. Only a hash whose
        /// single key is :receiver counts, so a genuine positional Hash still gets through.
        /// </summary>
        internal static bool TryTakeReceiverKeyword(ref object[]/*!*/ args, out object receiver) {
            receiver = null;
            if (args.Length == 0) {
                return false;
            }

            var hash = args[args.Length - 1] as IDictionary<object, object>;
            if (hash == null || hash.Count != 1) {
                return false;
            }

            foreach (var entry in hash) {
                var key = entry.Key as RubySymbol;
                if (key == null || key.ToString() != "receiver") {
                    return false;
                }
                receiver = entry.Value;
            }

            var rest = new object[args.Length - 1];
            Array.Copy(args, rest, rest.Length);
            args = rest;
            return true;
        }
    }

    [RubyException("NoMethodError", Extends = typeof(MissingMethodException), Inherits = typeof(MemberAccessException))]
    [HideMethod("message")]
    public static class NoMethodErrorOps {
        /// <summary>
        /// NoMethodError.new([message [, name [, args [, priv]]]] [, receiver: obj]).
        /// </summary>
        [RubyConstructor]
        public static MissingMethodException/*!*/ Factory(RubyClass/*!*/ self, params object[]/*!*/ allArgs) {
            object receiver;
            bool hasReceiver = NameErrorOps.TryTakeReceiverKeyword(ref allArgs, out receiver);
            if (allArgs.Length > 4) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..4)", allArgs.Length);
            }

            object message = allArgs.Length > 0 ? allArgs[0] : null;
            object name = allArgs.Length > 1 ? allArgs[1] : null;
            object args = allArgs.Length > 2 ? allArgs[2] : null;
            bool privateCall = allArgs.Length > 3 && RubyOps.IsTrue(allArgs[3]);

            MissingMethodException result = new MissingMethodException(RubyExceptionData.GetClrMessage(self, message));
            RubyExceptionData.InitializeException(result, message);

            var data = RubyExceptionData.GetInstance(result);
            data.Name = name;
            if (hasReceiver) {
                data.SetReceiver(receiver);
            }
            data.Arguments = args;
            data.PrivateCall = privateCall;
            return result;
        }

        /// <summary>
        /// True when the call that failed had an implicit receiver (or `self.`), the way a call to
        /// a private method may be written.
        /// </summary>
        [RubyMethod("private_call?")]
        public static bool IsPrivateCall(MissingMethodException/*!*/ self) {
            return RubyExceptionData.GetInstance(self).PrivateCall;
        }

        /// <summary>
        /// Records the arguments of the call that went missing, so that #args can answer them.
        /// </summary>
        internal static Exception/*!*/ SetArguments(Exception/*!*/ error, object[]/*!*/ args) {
            RubyExceptionData.GetInstance(error).Arguments = new RubyArray(args);
            return error;
        }

        [RubyMethod("args")]
        public static object GetArguments(MissingMethodException/*!*/ self) {
            return RubyExceptionData.GetInstance(self).Arguments;
        }
    }

    [RubyException("SecurityError", Extends = typeof(SecurityException), Inherits = typeof(Exception))]
    public static class SecurityErrorOps {
    }

    [RubyException("TypeError", Extends = typeof(InvalidOperationException), Inherits = typeof(SystemException))]
    public static class TypeErrorOps {
    }

    [RubyException("ZeroDivisionError", Extends = typeof(DivideByZeroException), Inherits = typeof(SystemException))]
    public static class ZeroDivisionErrorOps {
    }

    [RubyException("EncodingError", Extends = typeof(EncodingError), Inherits = typeof(SystemException))]
    public static class EncodingErrorOps {
    }

    [RubyException("RuntimeError", Extends = typeof(RuntimeError), Inherits = typeof(SystemException))]
    public static class RuntimeErrorOps {
    }

    [RubyException("UncaughtThrowError", Extends = typeof(UncaughtThrowError), Inherits = typeof(ArgumentException))]
    public static class UncaughtThrowErrorOps {
        /// <summary>UncaughtThrowError.new(tag, value, message = nil)</summary>
        [RubyConstructor]
        public static UncaughtThrowError/*!*/ Create(RubyClass/*!*/ self, object tag, object value, params object[]/*!*/ rest) {
            if (rest.Length > 1) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..1)", rest.Length);
            }
            object message = rest.Length == 1 ? rest[0] : null;
            if (message != null && !(message is MutableString)) {
                throw RubyExceptions.CreateImplicitConversionError(self.Context.GetClassDisplayName(message), "String");
            }
            var result = new UncaughtThrowError(message != null ? ((MutableString)message).ConvertToString() : null, tag, value);
            result.FormatsTag = true;
            result.HasMessage = message != null;
            RubyExceptionData.InitializeException(result, message);
            return result;
        }

        [RubyMethod("__ir_formats_tag__")]
        public static bool FormatsTag(UncaughtThrowError/*!*/ self) {
            return self.FormatsTag;
        }

        [RubyMethod("__ir_has_message__")]
        public static bool HasMessage(UncaughtThrowError/*!*/ self) {
            return self.HasMessage;
        }

        [RubyMethod("tag")]
        public static object Tag(UncaughtThrowError/*!*/ self) {
            return self.Tag;
        }

        [RubyMethod("value")]
        public static object Value(UncaughtThrowError/*!*/ self) {
            return self.Value;
        }
    }

    [RubyException("FrozenError", Extends = typeof(FrozenError), Inherits = typeof(RuntimeError))]
    public static class FrozenErrorOps {
        /// <summary>
        /// FrozenError.new([message] [, receiver: obj]) - the receiver keyword is recognised by
        /// shape, exactly as in NameErrorOps.
        /// </summary>
        [RubyConstructor]
        public static FrozenError/*!*/ Factory(RubyClass/*!*/ self, params object[]/*!*/ args) {
            object receiver;
            bool hasReceiver = NameErrorOps.TryTakeReceiverKeyword(ref args, out receiver);
            if (args.Length > 1) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..1)", args.Length);
            }

            object message = args.Length > 0 ? args[0] : null;
            var result = new FrozenError(RubyExceptionData.GetClrMessage(self, message));
            RubyExceptionData.InitializeException(result, message);
            if (hasReceiver) {
                result.SetReceiver(receiver);
            }
            return result;
        }

        [RubyMethod("receiver")]
        public static object Receiver(FrozenError/*!*/ self) {
            if (!self.HasReceiver) {
                throw RubyExceptions.CreateArgumentError("no receiver is available");
            }
            return self.Receiver;
        }
    }

    // special one:
    [RubyException("SystemCallError", Extends = typeof(ExternalException), Inherits = typeof(SystemException))]
    public static class SystemCallErrorOps {
        [RubyMethod("errno")]
        public static object Errno(RubyContext/*!*/ context, object/*!*/ self) {
            // `self` is deliberately `object`: several Errno classes are declared to inherit
            // from SystemCallError at the Ruby level but are backed by a CLR type that is not
            // an ExternalException (Errno::ENOENT is a FileNotFoundException), so typing the
            // parameter as ExternalException made #errno a TypeError on exactly those.
            //
            // The CLR-backed Errno classes also carry no errno at all -- ErrorCode is then the
            // generic COM HRESULT 0x80004005. Every Errno class does have its number in an
            // Errno constant (see Src/StdLib/ironruby/ruby4.rb), so prefer that. A plain
            // SystemCallError has no number, and MRI answers nil there.
            object number;
            if (context.GetClassOf(self).TryGetConstant(null, "Errno", out number) && number is int) {
                return number;
            }
            var external = self as ExternalException;
            if (external != null && external.ErrorCode != unchecked((int)0x80004005)) {
                return external.ErrorCode;
            }
            return null;
        }
        
        [RubyConstructor]
        public static ExternalException/*!*/ Factory(RubyClass/*!*/ self, [DefaultProtocol]MutableString message) {
            ExternalException result = new ExternalException(RubyExceptions.MakeMessage(ref message, "unknown error"));
            RubyExceptionData.InitializeException(result, message);
            return result;
        }

        [RubyConstructor]
        public static ExternalException/*!*/ Factory(RubyClass/*!*/ self, int errorCode) {
            switch (errorCode) {
                // TODO:
                //case 0: return RubyExceptions.CreateNOERROR();
                //case 1: return RubyExceptions.CreateEPERM();
                //case 2: return RubyExceptions.CreateENOENT(); TODO: types don't match
                //case 3: return RubyExceptions.CreateESRCH();
                //case 4: return RubyExceptions.CreateEINTR();
                //case 5: return RubyExceptions.CreateEIO();
                //case 6: return RubyExceptions.CreateENXIO();
                //case 7: return RubyExceptions.CreateE2BIG();
                //case 8: return RubyExceptions.CreateENOEXEC(); 
                //case 9: return RubyExceptions.CreateEBADF();     TODO: types don't match
                //case 10: return new Errno.ChildError();
                //case 11: return RubyExceptions.CreateEAGAIN();
                //case 12: return RubyExceptions.CreateENOMEM();
                //case 13: return RubyExceptions.CreateEACCES();    TODO: types don't match
                //case 14: return RubyExceptions.CreateEFAULT();
                //case 15: break;
                //case 16: return RubyExceptions.CreateEBUSY();
                //case 17: return RubyExceptions.CreateEEXIST();    TODO: types don't match
                //case 18: return RubyExceptions.CreateEXDEV();
                //case 19: return RubyExceptions.CreateENODEV();
                //case 20: return RubyExceptions.CreateENOTDIR();
                //case 21: return RubyExceptions.CreateEISDIR();
                //case 22: return RubyExceptions.CreateEINVAL();     TODO: types don't match
                //case 23: return RubyExceptions.CreateENFILE();
                //case 24: return RubyExceptions.CreateEMFILE();
                //case 25: return RubyExceptions.CreateENOTTY();
                //case 26: break;
                //case 27: return RubyExceptions.CreateEFBIG();
                //case 28: return RubyExceptions.CreateENOSPC();
                //case 29: return RubyExceptions.CreateESPIPE();
                //case 30: return RubyExceptions.CreateEROFS();
                //case 31: return RubyExceptions.CreateEMLINK();
                //case 32: return RubyExceptions.CreateEPIPE();
                //case 33: return RubyExceptions.CreateEDOM();
                //case 34: return RubyExceptions.CreateERANGE();
                //case 35: break;
                //case 36: return RubyExceptions.CreateEDEADLK();
                //case 37: break;
                //case 38: return RubyExceptions.CreateENAMETOOLONG();
                //case 39: return RubyExceptions.CreateENOLCK();
                //case 40: return RubyExceptions.CreateENOSYS();
                //case 41: return RubyExceptions.CreateENOTEMPTY();
                //case 42: return RubyExceptions.CreateEILSEQ();

                // case 10035: return RubyExceptions.CreateEWOULDBLOCK();
                // case 10036: return RubyExceptions.CreateEINPROGRESS();
                // case 10037: return RubyExceptions.CreateEALREADY();
                // case 10038: return RubyExceptions.CreateENOTSOCK();
                // case 10039: return RubyExceptions.CreateEDESTADDRREQ();
                // case 10040: return RubyExceptions.CreateEMSGSIZE();
                // case 10041: return RubyExceptions.CreateEPROTOTYPE();
                // case 10042: return RubyExceptions.CreateENOPROTOOPT();
                // case 10043: return RubyExceptions.CreateEPROTONOSUPPORT();
                // case 10044: return RubyExceptions.CreateESOCKTNOSUPPORT();
                // case 10045: return RubyExceptions.CreateEOPNOTSUPP();
                // case 10046: return RubyExceptions.CreateEPFNOSUPPORT();
                // case 10047: return RubyExceptions.CreateEAFNOSUPPORT();
                case 10048: return new Errno.AddressInUseError();
                // case 10049: return RubyExceptions.CreateEADDRNOTAVAIL();
                // case 10050: return RubyExceptions.CreateENETDOWN();
                // case 10051: return RubyExceptions.CreateENETUNREACH();
                // case 10052: return RubyExceptions.CreateENETRESET();
                case 10053: return new Errno.ConnectionAbortedError();
                case 10054: return new Errno.ConnectionResetError();
                // case 10055: return RubyExceptions.CreateENOBUFS();
                // case 10056: return RubyExceptions.CreateEISCONN();
                case 10057: return new Errno.NotConnectedError();
                // case 10058: return RubyExceptions.CreateESHUTDOWN();
                // case 10059: return RubyExceptions.CreateETOOMANYREFS();
                // case 10060: return RubyExceptions.CreateETIMEDOUT();
                case 10061: return new Errno.ConnectionRefusedError();
                // case 10062: return RubyExceptions.CreateELOOP();
                case 10064: return new Errno.HostDownError();
                // case 10065: return RubyExceptions.CreateEHOSTUNREACH();
                // case 10068: return RubyExceptions.CreateEUSERS();
                // case 10069: return RubyExceptions.CreateEDQUOT();
                // case 10070: return RubyExceptions.CreateESTALE();
                // case 10071: return RubyExceptions.CreateEREMOTE();
            }

            var message = MutableString.CreateAscii("Unknown Error");
            ExternalException result = new ExternalException(RubyExceptions.MakeMessage(ref message, "Unknown Error"), errorCode);
            RubyExceptionData.InitializeException(result, message);
            return result;
        }
    }
}

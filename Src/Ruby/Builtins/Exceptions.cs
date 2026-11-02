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
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using System.Runtime.InteropServices;


namespace IronRuby.Builtins {
    public class LocalJumpError : SystemException {
        private readonly RuntimeFlowControl _skipFrame;

        /// <summary>
        /// The exception cannot be rescued in this frame if set.
        /// </summary>
        internal RuntimeFlowControl SkipFrame {
            get { return _skipFrame; }
        }

        private readonly string _reason;

        private readonly object _exitValue;

        /// <summary>
        /// LocalJumpError#reason: the jump that had nowhere to go ("break", "next", "redo",
        /// "retry" or "return"), or null for MRI's :noreason.
        /// </summary>
        public string Reason {
            get { return _reason; }
        }

        /// <summary>
        /// LocalJumpError#exit_value: the value the jump carried.
        /// </summary>
        public object ExitValue {
            get { return _exitValue; }
        }

        internal LocalJumpError(string/*!*/ message, RuntimeFlowControl/*!*/ skipFrame)
            : this(message, (Exception)null) {
            Assert.NotNull(message, skipFrame);
            _skipFrame = skipFrame;
        }

        internal LocalJumpError(string/*!*/ message, string/*!*/ reason, object exitValue)
            : this(message, (Exception)null) {
            _reason = reason;
            _exitValue = exitValue;
        }

        internal LocalJumpError(string/*!*/ message, RuntimeFlowControl/*!*/ skipFrame, string/*!*/ reason, object exitValue)
            : this(message, skipFrame) {
            _reason = reason;
            _exitValue = exitValue;
        }

        public LocalJumpError() : this(null, (Exception)null) { }
        public LocalJumpError(string message) : this(message, (Exception)null) { }
        public LocalJumpError(string message, Exception inner) : base(message, inner) { }
    }

    public class SystemExit : Exception {
        private int _status;

        /// <summary>
        /// Settable because a subclass is built by #allocate and #initialize rather than by the
        /// constructor, and its #initialize is where the status arrives.
        /// </summary>
        public int Status {
            get { return _status; }
            set { _status = value; }
        }

        public SystemExit(int status, string message)
            : this(message) {
            _status = status;
        }

        public SystemExit(int status) 
            : this() {
            _status = status;
        }

        public SystemExit() : this(null, null) { }
        public SystemExit(string message) : this(message, null) { }
        public SystemExit(string message, Exception inner) : base(message, inner) { }
    }

    public class ScriptError : Exception {
        public ScriptError() : this(null, null) { }
        public ScriptError(string message) : this(message, null) { }
        public ScriptError(string message, Exception inner) : base(message, inner) { }
    }

    public class NotImplementedError : ScriptError {
        public NotImplementedError() : this(null, null) { }
        public NotImplementedError(string message) : this(message, null) { }
        public NotImplementedError(string message, Exception inner) : base(message, inner) { }
    }

    public class LoadError : ScriptError {
        public LoadError() : this(null, null) { }
        public LoadError(string message) : this(message, null) { }
        public LoadError(string message, Exception inner) : base(message, inner) { }
    }

    public class SystemStackError : SystemException {
        public SystemStackError() : this(null, null) { }
        public SystemStackError(string message) : this(message, null) { }
        public SystemStackError(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Ruby's IndexError. Not System.IndexOutOfRangeException, which is sealed: KeyError and
    /// StopIteration (and Ruby classes) have to be able to subclass it.
    /// </summary>
    public class IndexError : SystemException {
        public IndexError() : this(null, null) { }
        public IndexError(string message) : this(message, null) { }
        public IndexError(string message, Exception inner) : base(message, inner) { }
    }

    public class RegexpError : SystemException {
        public RegexpError() : this(null, null) { }
        public RegexpError(string message) : this(message, null) { }
        public RegexpError(string message, Exception inner) : base(message, inner) { }
    }

    public class EncodingError : SystemException {
        public EncodingError() : this(null, null) { }
        public EncodingError(string message) : this(message, null) { }
        public EncodingError(string message, Exception inner) : base(message, inner) { }
    }

    public class EncodingCompatibilityError : EncodingError {
        public EncodingCompatibilityError() : this(null, null) { }
        public EncodingCompatibilityError(string message) : this(message, null) { }
        public EncodingCompatibilityError(string message, Exception inner) : base(message, inner) { }
    }

    public class UndefinedConversionError : EncodingError {
        public UndefinedConversionError() : this(null, null) { }
        public UndefinedConversionError(string message) : this(message, null) { }
        public UndefinedConversionError(string message, Exception inner) : base(message, inner) { }

        /// <summary>The encoding the failing conversion stage read from. May be null.</summary>
        private RubyEncoding _sourceEncoding, _destinationEncoding;

        private byte[] _errorCharBytes;

        public RubyEncoding SourceEncoding { get { return _sourceEncoding; } set { _sourceEncoding = value; } }
        public RubyEncoding DestinationEncoding { get { return _destinationEncoding; } set { _destinationEncoding = value; } }

        /// <summary>The character that could not be represented, in the bytes of the stage's source encoding.</summary>
        public byte[] ErrorCharBytes { get { return _errorCharBytes; } set { _errorCharBytes = value; } }
    }

    public class InvalidByteSequenceError : EncodingError {
        public InvalidByteSequenceError() : this(null, null) { }
        public InvalidByteSequenceError(string message) : this(message, null) { }
        public InvalidByteSequenceError(string message, Exception inner) : base(message, inner) { }

        private RubyEncoding _sourceEncoding, _destinationEncoding;

        private byte[] _errorBytes, _readAgainBytes;

        // Nullable: an error object that was not produced by a conversion has not been told
        // either way, and MRI's #incomplete_input? answers nil for it.
        private bool? _incompleteInput;

        public RubyEncoding SourceEncoding { get { return _sourceEncoding; } set { _sourceEncoding = value; } }
        public RubyEncoding DestinationEncoding { get { return _destinationEncoding; } set { _destinationEncoding = value; } }

        /// <summary>The bytes that were rejected.</summary>
        public byte[] ErrorBytes { get { return _errorBytes; } set { _errorBytes = value; } }

        /// <summary>The bytes that should be read again after the error.</summary>
        public byte[] ReadAgainBytes { get { return _readAgainBytes; } set { _readAgainBytes = value; } }

        /// <summary>True if the input ended in the middle of a character rather than being invalid.</summary>
        public bool? IncompleteInput { get { return _incompleteInput; } set { _incompleteInput = value; } }
    }

    public class ConverterNotFoundError : EncodingError {
        public ConverterNotFoundError() : this(null, null) { }
        public ConverterNotFoundError(string message) : this(message, null) { }
        public ConverterNotFoundError(string message, Exception inner) : base(message, inner) { }
    }

    public class RuntimeError : SystemException {
        public RuntimeError() : this(null, null) { }
        public RuntimeError(string message) : this(message, null) { }
        public RuntimeError(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Raised when a frozen object is modified. MRI made this a subclass of RuntimeError
    /// in 2.5, so `rescue RuntimeError` still catches it.
    ///
    /// The type has to live here in Src/Ruby rather than in Src/Libraries: the core
    /// assembly cannot reference the library one, and RubyExceptions.CreateObjectFrozenError
    /// is in the core. The *registration* is the usual one-liner in
    /// Src/Libraries/Builtins/Exceptions.cs, following EBADF/EEXIST/RuntimeError.
    /// </summary>
    /// <summary>
    /// UncaughtThrowError (Ruby 2.2), raised by Kernel#throw when no Kernel#catch is waiting for
    /// the tag. It derives from ArgumentError, which is what `rescue ArgumentError` around a
    /// stray throw has always relied on, and carries the tag and value MRI exposes.
    ///
    /// Like FrozenError the type lives here rather than in Src/Libraries, because
    /// KernelOps.Throw's counterpart in the core needs it; the registration is the usual
    /// one-liner in Src/Libraries/Builtins/Exceptions.cs.
    /// </summary>
    public class UncaughtThrowError : ArgumentException {
        private readonly object _tag;
        private readonly object _value;

        public UncaughtThrowError() : this(null, null) { }
        public UncaughtThrowError(string message) : this(message, null) { }
        public UncaughtThrowError(string message, Exception inner) : base(message, inner) { }

        public UncaughtThrowError(string message, object tag, object value) : base(message) {
            _tag = tag;
            _value = value;
        }

        /// <summary>
        /// Set by UncaughtThrowError.new(tag, value, message): #to_s then formats the message with
        /// the tag (MRI's uncaught_throw_to_s), and a missing message is kept apart from an empty one.
        /// </summary>
        public bool FormatsTag { get; set; }
        public bool HasMessage { get; set; }

        public object Tag {
            get { return _tag; }
        }

        public object Value {
            get { return _value; }
        }
    }

    public class FrozenError : RuntimeError {
        private object _receiver;
        private bool _hasReceiver;

        public FrozenError() : this(null, null) { }
        public FrozenError(string message) : this(message, null) { }
        public FrozenError(string message, Exception inner) : base(message, inner) { }

        /// <summary>
        /// The object that was being modified, exposed as FrozenError#receiver.
        /// </summary>
        public object Receiver {
            get { return _receiver; }
        }

        public bool HasReceiver {
            get { return _hasReceiver; }
        }

        public FrozenError SetReceiver(object receiver) {
            _receiver = receiver;
            _hasReceiver = true;
            return this;
        }
    }

    public class SyntaxError : ScriptError {
        private readonly string _file;
        private readonly string _lineSourceCode;
        private readonly int _line;
        private readonly int _column;
        private readonly bool _hasLineInfo;

        public SyntaxError() : this(null, null) { }
        public SyntaxError(string message) : this(message, null) { }
        public SyntaxError(string message, Exception inner) : base(message, inner) { }

        /// <summary>
        /// The file the source came from, which SyntaxError#path reports.
        /// </summary>
        public string File {
            get { return _file; }
        }

        internal int Line {
            get { return _line; }
        }

        internal int Column {
            get { return _column; }
        }

        internal string LineSourceCode {
            get { return _lineSourceCode; }
        }

        internal bool HasLineInfo {
            get { return _hasLineInfo; }
        }

        internal SyntaxError(string/*!*/ message, string file, int line, int column, string lineSourceCode) 
            : base(message) {
            _file = file;
            _line = line;
            _column = column;
            _lineSourceCode = lineSourceCode;
            _hasLineInfo = true;
        }
    }

    public class ExistError : ExternalException {
        private const string/*!*/ M = "File exists";

        public ExistError() : this(null, null) { }
        public ExistError(string message) : this(message, null) { }
        public ExistError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public ExistError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
    }

    public class BadFileDescriptorError : ExternalException {
        private const string/*!*/ M = "Bad file descriptor";

        public BadFileDescriptorError() : this(null, null) { }
        public BadFileDescriptorError(string message) : this(message, null) { }
        public BadFileDescriptorError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public BadFileDescriptorError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
    }

    /// <summary>
    /// Backs Errno::EISDIR. .NET has no "is a directory" exception -- opening a
    /// directory throws UnauthorizedAccessException, which maps to EACCES -- so the
    /// runtime needs a type of its own to raise. Registered in Errno.cs.
    /// </summary>
    public class DirectoryIsError : ExternalException {
        private const string/*!*/ M = "Is a directory";

        public DirectoryIsError() : this(null, null) { }
        public DirectoryIsError(string message) : this(message, null) { }
        public DirectoryIsError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public DirectoryIsError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
    }

    public class ExecFormatError : ExternalException {
        private const string/*!*/ M = "Exec format error";

        public ExecFormatError() : this(null, null) { }
        public ExecFormatError(string message) : this(message, null) { }
        public ExecFormatError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public ExecFormatError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
    }

    /// <summary>
    /// Backs Errno::ENXIO. .NET reports open(2)'s ENXIO - no controlling terminal for
    /// /dev/tty, a FIFO opened for writing with no reader - as a bare IOException, which
    /// would otherwise surface as IOError. Registered in Errno.cs.
    /// </summary>
    public class NoSuchDeviceOrAddressError : ExternalException {
        private const string/*!*/ M = "No such device or address";

        public NoSuchDeviceOrAddressError() : this(null, null) { }
        public NoSuchDeviceOrAddressError(string message) : this(message, null) { }
        public NoSuchDeviceOrAddressError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public NoSuchDeviceOrAddressError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
    }

    /// <summary>
    /// Backs Errno::EIO. read(2) on a pty master answers EIO on Linux once every descriptor
    /// on the slave side is closed - which is how a program driving a child through PTY.spawn
    /// learns that the child has gone - and Ruby programs rescue Errno::EIO for exactly that.
    /// A bare IOException would surface as IOError instead. Registered in Errno.cs.
    /// </summary>
    public class InputOutputError : ExternalException {
        private const string/*!*/ M = "Input/output error";

        public InputOutputError() : this(null, null) { }
        public InputOutputError(string message) : this(message, null) { }
        public InputOutputError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public InputOutputError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
    }

    public class InvalidError : ExternalException {
        private const string/*!*/ M = "Invalid argument";

        public InvalidError() : this(null, null) { }
        public InvalidError(string message) : this(message, null) { }
        public InvalidError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public InvalidError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
    }
}


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

using System.Runtime.InteropServices;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Actions;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Utils;
using System;
using System.IO;
using System.Numerics;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using IronRuby.Runtime.Conversions;

namespace IronRuby.StandardLibrary.StringIO {
    [RubyClass("StringIO", Inherits = typeof(object)), Includes(typeof(Enumerable))]
    public class StringIO {
        // nil once StringIO.open's block has finished: MRI drops the string then, so #string
        // answers nil rather than the text that was there.
        private MutableString _content;
        private int _position;
        private IOMode _mode;

        // What the stream was opened as, which never changes. #close_read and #close_write ask
        // whether this end ever existed - MRI calls closing one that did not "non-duplex" and
        // refuses - while closing an end that is merely already closed is a no-op.
        private IOMode _initialMode;

        private int _lineNumber;

        // One write is one step. MRI gets that from the GVL; here it has to be said, or two
        // threads read the same position and write over each other's bytes.
        private readonly object/*!*/ _mutex = new object();

        // What #set_encoding was told, when it was told anything. Normally the encoding is the
        // string's own, but the string may be frozen - and MRI still remembers the answer then
        // rather than refusing, so it cannot live only on the string.
        private RubyEncoding _externalEncoding;

        // The version of MRI's stringio whose behaviour this follows.  It is not decoration:
        // ruby/spec guards several expectations on it, because stringio changed what #read does
        // with the encoding of a given buffer at 3.1.2.
        [RubyConstant("VERSION")]
        public static readonly MutableString/*!*/ Version = MutableString.CreateAscii("3.2.0").Freeze();

        public StringIO()
            : this(MutableString.CreateEmpty(), IOMode.ReadWrite) {
        }

        public StringIO(RubyContext/*!*/ context)
            : this(MutableString.CreateEmpty(context.DefaultExternalEncoding), IOMode.ReadWrite) {
        }

        public StringIO(MutableString/*!*/ content, IOMode mode) {
            ContractUtils.RequiresNotNull(content, "content");
            _content = content;
            _mode = mode;
            _initialMode = mode;
        }

        private void SetPosition(long value) {
            if (value < 0 || value > Int32.MaxValue) {
                throw RubyExceptions.CreateEINVAL();
            }
            _position = (int)value; 
        }

        private void SetContent(MutableString content) {
            _content = content;
            _position = 0;
            _lineNumber = 0;
            _externalEncoding = null;
        }

        private MutableString/*!*/ GetContent() {
            if (_mode.IsClosed() || _content == null) {
                throw RubyExceptions.CreateIOError("closed stream");
            }
            return _content;
        }

        private MutableString/*!*/ GetReadableContent() {
            if (!_mode.CanRead() || _content == null) {
                throw RubyExceptions.CreateIOError("not opened for reading");
            }
            return _content;
        }

        private MutableString/*!*/ GetWritableContent() {
            if (!_mode.CanWrite() || _content == null) {
                throw RubyExceptions.CreateIOError("not opened for writing");
            }
            return _content;
        }

        private void Close() {
            _mode = _mode.Close();
        }

        private static MutableString/*!*/ CheckContent(MutableString/*!*/ content, IOMode mode) {
            if (content.IsFrozen && mode.CanWrite()) {
                throw Errno.CreateEACCES("Permission denied");
            }

            if ((mode & IOMode.Truncate) != 0) {
                content.Clear();
            }
            return content;
        }

        #region Construction

        /// <summary>
        /// StringIO.new(string = "", mode = nil, **options). IronRuby has no real keyword
        /// arguments - they arrive as a trailing Hash - so the options are taken off the end the
        /// same way #gets takes its chomp:.
        /// </summary>
        [RubyConstructor]
        public static StringIO/*!*/ Create(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<int?>/*!*/ toInt, ConversionStorage<MutableString>/*!*/ toStr,
            BlockParam block, RubyClass/*!*/ self,
            [Optional]object content, [Optional]object mode,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            // MRI does not yield to a block here and says so rather than swallowing it.
            if (block != null) {
                self.Context.ReportWarning("StringIO::new() does not take block; use StringIO::open() instead");
            }

            var result = new StringIO(self.Context);
            Initialize(toHash, toInt, toStr, self.Context, result, content, mode, options);
            return result;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static StringIO/*!*/ Reinitialize(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<int?>/*!*/ toInt, ConversionStorage<MutableString>/*!*/ toStr,
            RubyContext/*!*/ context, StringIO/*!*/ self,
            [Optional]object content, [Optional]object mode,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            Initialize(toHash, toInt, toStr, context, self, content, mode, options);
            return self;
        }

        /// <summary>Whether a mode asks for binary - "rb", "wb+" - reading only its flags, not
        /// the encodings that may follow a colon.</summary>
        private static bool IsBinaryMode(MutableString mode) {
            if (mode == null) {
                return false;
            }
            string text = mode.ToString();
            int end = text.IndexOf(':');
            if (end < 0) {
                end = text.Length;
            }
            return text.IndexOf('b', 0, end) >= 0;
        }

        private static void Initialize(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<int?>/*!*/ toInt, ConversionStorage<MutableString>/*!*/ toStr,
            RubyContext/*!*/ context, StringIO/*!*/ self, object content, object mode,
            IDictionary<object, object> options) {

            Protocols.TryConvertToOptions(toHash, ref options, ref content, ref mode);

            bool hasContent = content != Missing.Value;
            bool hasMode = mode != Missing.Value;

            IOInfo info = new IOInfo();
            bool binary = false;

            // A nil mode is MRI's "no mode here", which leaves a :mode option free to give one.
            // The conversion happens once: the mode may be a mock that expects a single #to_str.
            if (hasMode && mode != null) {
                var toIntSite = toInt.GetSite(TryConvertToFixnumAction.Make(context));
                int? numeric = toIntSite.Target(toIntSite, mode);
                if (numeric.HasValue) {
                    info = new IOInfo(IOModeNative.ToIOMode(numeric.Value));
                } else {
                    MutableString text = Protocols.CastToString(toStr, mode);
                    info = IOInfo.Parse(context, text);
                    binary = IsBinaryMode(text);
                }
            }

            if (options != null) {
                // IOInfo knows the conflicts MRI refuses - an encoding or a binmode said twice,
                // textmode and binmode at once - and they are the same ones here.
                info = info.AddOptions(toStr, options);

                object value;
                if (options.TryGetValue(context.CreateAsciiSymbol("binmode"), out value) && Protocols.IsTrue(value)) {
                    binary = true;
                }
                if (options.TryGetValue(context.CreateAsciiSymbol("mode"), out value)) {
                    binary |= IsBinaryMode(value as MutableString);
                }
            }

            MutableString str = hasContent
                ? Protocols.CastToString(toStr, content)
                : MutableString.CreateEmpty(context.DefaultExternalEncoding);

            IOMode ioMode = info.HasMode
                ? info.Mode
                : (str.IsFrozen ? IOMode.ReadOnly : IOMode.ReadWrite);
            ioMode |= IOMode.PreserveEndOfLines;

            self.SetContent(CheckContent(str, ioMode));
            self._mode = ioMode;
            self._initialMode = ioMode;

            // Measured against CRuby 4.0: the encoding the mode or the options ask for is only
            // taken when the mode slot was there to ask in.  StringIO.new(str, encoding: 'X')
            // keeps the string's own encoding, StringIO.new(str, nil, encoding: 'X') does not,
            // and neither does StringIO.new(encoding: 'X') - which has no string to ask.
            // The string itself is never re-tagged: only the stream's answer changes.
            if (!hasContent || hasMode) {
                RubyEncoding encoding = info.InternalEncoding ?? info.ExternalEncoding;
                if (encoding == null && binary) {
                    encoding = RubyEncoding.Binary;
                }
                if (encoding != null) {
                    self._externalEncoding = encoding;
                }
            }
        }

        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ Open() {
            return RubyIOOps.Open();
        }

        #endregion

        #region reopen

        [RubyMethod("reopen")]
        public static StringIO/*!*/ Reopen(StringIO/*!*/ self) {
            self.SetContent(MutableString.CreateBinary());
            self._mode = self._initialMode = IOMode.ReadWrite;
            return self;
        }

        [RubyMethod("reopen")]
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static StringIO/*!*/ Reopen(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ toStringIoStorage,
            StringIO/*!*/ self, [NotNull]object/*!*/ other) {

            if (!Protocols.RespondTo(respondToStorage, other, "to_strio")) {
                throw RubyExceptions.CreateImplicitConversionError(respondToStorage.Context.GetClassName(other), "StringIO");
            }

            var site = toStringIoStorage.GetCallSite("to_strio", 0);
            var strio = site.Target(site, other) as StringIO;
            if (strio == null) {
                throw RubyExceptions.CreateTypeError("C#to_strio should return StringIO");
            }

            return Reopen(respondToStorage.Context, self, strio);
        }

        [RubyMethod("reopen")]
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static StringIO/*!*/ Reopen(RubyContext/*!*/ context, [NotNull]StringIO/*!*/ self, [NotNull]StringIO/*!*/ other) {
            self.SetContent(other._content);
            self._mode = other._mode;
            self._initialMode = other._initialMode;
            self._lineNumber = other._lineNumber;
            self._position = other._position;

            // TODO: this seems to be MRI bug
            // Shouldn't StringIO's taint be always same as the underlying string's taint?
            context.TaintObjectBy(self, other);
            return self;
        }

        [RubyMethod("reopen")]
        public static StringIO/*!*/ Reopen(StringIO/*!*/ self, [NotNull]MutableString/*!*/ content) {
            return Reopen(self, content, null);
        }

        [RubyMethod("reopen")]
        public static StringIO/*!*/ Reopen(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ content,
            [DefaultProtocol, NotNull]MutableString mode) {
            IOMode ioMode = IOModeEnum.Parse(mode, content.IsFrozen ? IOMode.ReadOnly : IOMode.ReadWrite) | IOMode.PreserveEndOfLines;
            self.SetContent(CheckContent(content, ioMode));
            self._mode = self._initialMode = ioMode;
            return self;
        }

        [RubyMethod("reopen")]
        public static StringIO/*!*/ Reopen(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ content, int mode) {
            IOMode ioMode = (IOMode)mode | IOMode.PreserveEndOfLines;
            self.SetContent(CheckContent(content, ioMode));
            self._mode = self._initialMode = ioMode;
            return self;
        }

        #endregion

        #region close(_read|_write), closed(_read|_write)?

        // MRI: closing a StringIO that is already closed is not an error.
        [RubyMethod("close")]
        public static void Close(StringIO/*!*/ self) {
            self.Close();
        }

        /// <summary>
        /// The complaint is about an end that never existed - MRI's "non-duplex" - and not about
        /// one that is merely already closed, which is a no-op answering nil.  That is why the
        /// question is put to the mode the stream was opened with rather than its current one.
        /// </summary>
        [RubyMethod("close_read")]
        public static void CloseRead(StringIO/*!*/ self) {
            if (!self._initialMode.CanRead()) {
                throw RubyExceptions.CreateIOError("closing non-duplex IO for reading");
            }
            self._mode = self._mode.CloseRead();
        }

        [RubyMethod("close_write")]
        public static void CloseWrite(StringIO/*!*/ self) {
            if (!self._initialMode.CanWrite()) {
                throw RubyExceptions.CreateIOError("closing non-duplex IO for writing");
            }
            self._mode = self._mode.CloseWrite();
        }

        [RubyMethod("closed?")]
        public static bool IsClosed(StringIO/*!*/ self) {
            return self._mode.IsClosed();
        }

        [RubyMethod("closed_read?")]
        public static bool IsClosedRead(StringIO/*!*/ self) {
            return !self._mode.CanRead();
        }

        [RubyMethod("closed_write?")]
        public static bool IsClosedWrite(StringIO/*!*/ self) {
            return !self._mode.CanWrite();
        }

        #endregion

        #region length, size, pos, tell, truncate, eof, eof?, rewind, seek

        [RubyMethod("length")]
        [RubyMethod("size")]
        public static int GetLength(StringIO/*!*/ self) {
            return self.GetContent().GetByteCount();
        }

        [RubyMethod("pos")]
        [RubyMethod("tell")]
        public static int GetPosition(StringIO/*!*/ self) {
            return self._position;
        }

        [RubyMethod("pos=")]
        public static void Pos(StringIO/*!*/ self, [DefaultProtocol]int pos) {
            self.SetPosition(pos);
        }

        [RubyMethod("truncate")]
        public static object SetLength(ConversionStorage<int>/*!*/ fixnumCast, StringIO/*!*/ self, object lengthObj) {
            int length = Protocols.CastToFixnum(fixnumCast, lengthObj);
            if (length < 0) {
                throw RubyExceptions.CreateEINVAL("negative length");
            }
            self.GetWritableContent().SetByteCount(length);
            // MRI answers 0, whatever the length
            return 0;
        }

        // MRI's strio_rewind does not check that the stream is open: a StringIO that a
        // GzipWriter has closed can still be rewound, and its #string read back.
        [RubyMethod("rewind")]
        public static int Rewind(StringIO/*!*/ self) {
            self._position = 0;
            self._lineNumber = 0;
            return 0;
        }

        /// <summary>
        /// Unlike #pos= and #rewind, which MRI still answers on a closed stream, #seek wants an
        /// open one.  A whence that is none of the three is Errno::EINVAL - the error the system
        /// call would give - rather than an ArgumentError.
        /// </summary>
        [RubyMethod("seek")]
        public static int Seek(StringIO/*!*/ self, [DefaultProtocol]int pos, [DefaultProtocol, DefaultParameterValue(RubyIO.SEEK_SET)]int seekOrigin) {
            MutableString content = self.GetContent();

            SeekOrigin origin;
            switch (seekOrigin) {
                case RubyIO.SEEK_SET: origin = SeekOrigin.Begin; break;
                case RubyIO.SEEK_CUR: origin = SeekOrigin.Current; break;
                case RubyIO.SEEK_END: origin = SeekOrigin.End; break;
                default: throw RubyExceptions.CreateEINVAL("invalid whence");
            }

            self.SetPosition(RubyIO.GetSeekPosition(content.GetByteCount(), self._position, pos, origin));
            return 0;
        }

        [RubyMethod("eof")]
        [RubyMethod("eof?")]
        public static bool Eof(StringIO/*!*/ self) {
            var context = self.GetReadableContent();
            return self._position >= context.GetByteCount();
        }

        #endregion

        #region string, string=

        [RubyMethod("string")]
        public static MutableString GetString(StringIO/*!*/ self) {
            return self._content;
        }

        [RubyMethod("string=")]
        public static MutableString/*!*/ SetString(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            self.SetContent(str);
            return str;
        }

        #endregion

        #region <<, print, putc, puts

        [RubyMethod("<<")]
        public static object/*!*/ Output(BinaryOpStorage/*!*/ writeStorage, object/*!*/ self, object value) {
            return PrintOps.Output(writeStorage, self, value);
        }

        // With no arguments MRI prints $_, and it prints it as a string: nil's string is empty,
        // not "nil".
        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, RubyScope/*!*/ scope, object self) {
            Print(writeStorage, self, (object)scope.GetInnerMostClosureScope().LastInputLine);
        }

        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object self, params object[]/*!*/ args) {
            // MRI: StringIO#print is different from PrintOps.Print - it doesn't output delimiter after each arg.
            MutableString delimiter = writeStorage.Context.OutputSeparator;

            foreach (object arg in args) {
                Protocols.Write(writeStorage, self, arg ?? MutableString.CreateEmpty());
            }

            if (delimiter != null) {
                Protocols.Write(writeStorage, self, delimiter);
            }
        }

        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object/*!*/ self, object value) {
            Protocols.Write(writeStorage, self, value ?? MutableString.CreateEmpty());

            MutableString delimiter = writeStorage.Context.OutputSeparator;
            if (delimiter != null) {
                Protocols.Write(writeStorage, self, delimiter);
            }
        }

        [RubyMethod("putc")]
        public static MutableString/*!*/ Putc(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ val) {
            return PrintOps.Putc(writeStorage, self, val);
        }

        [RubyMethod("putc")]
        public static object Putc(ConversionStorage<int>/*!*/ fixnumCast, BinaryOpStorage/*!*/ writeStorage, object self, object c) {
            return PrintOps.Putc(fixnumCast, writeStorage, self, c);
        }

        [RubyMethod("puts")]
        public static void PutsEmptyLine(BinaryOpStorage/*!*/ writeStorage, object self) {
            PrintOps.PutsEmptyLine(writeStorage, self);
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ str) {
            PrintOps.Puts(writeStorage, self, str);
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion, 
            ConversionStorage<IList>/*!*/ tryToAry, object self, [NotNull]object/*!*/ val) {

            PrintOps.Puts(writeStorage, tosConversion, tryToAry, self, val);
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            ConversionStorage<IList>/*!*/ tryToAry, object self, params object[]/*!*/ vals) {

            PrintOps.Puts(writeStorage, tosConversion, tryToAry, self, vals);
        }

        [RubyMethod("printf")]
        public static void PrintFormatted(
            StringFormatterSiteStorage/*!*/ storage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            BinaryOpStorage/*!*/ writeStorage,
            StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {

            PrintOps.PrintFormatted(storage, stringCast, writeStorage, self, format, args);
        }

        #endregion

        #region write, syswrite

        /// <summary>
        /// MRI converts what is written into the stream's external encoding.  Not when either
        /// side is BINARY - bytes are bytes then - and not when the conversion cannot be made:
        /// an unconvertible character does not raise here, the string goes in as it stands.
        /// </summary>
        private static MutableString/*!*/ Transcode(StringIO/*!*/ self, MutableString/*!*/ value) {
            RubyEncoding to = GetExternalEncoding(self);
            RubyEncoding from = value.Encoding;
            if (to == null || from == null || ReferenceEquals(to, from)) {
                return value;
            }
            if (ReferenceEquals(to, RubyEncoding.Binary) || ReferenceEquals(from, RubyEncoding.Binary)) {
                return value;
            }

            try {
                return MutableString.CreateBinary(to.StrictEncoding.GetBytes(value.ConvertToString()), to);
            } catch (EncoderFallbackException) {
                return value;
            } catch (DecoderFallbackException) {
                return value;
            }
        }

        [RubyMethod("write")]
        [RubyMethod("syswrite")]
        public static int Write(StringIO/*!*/ self, [NotNull]MutableString/*!*/ value) {
            var content = self.GetWritableContent();
            value = Transcode(self, value);
            var bytesWritten = value.GetByteCount();

            // Reading the position, writing at it and moving it on is one step: MRI gets that
            // from the GVL, and without saying so here two threads write over each other.
            lock (self._mutex) {
                int pos = ((self._mode & IOMode.WriteAppends) != 0) ? content.GetByteCount() : self._position;

                try {
                    content.WriteBytes(pos, value, 0, bytesWritten);
                } catch (InvalidOperationException) {
                    throw RubyExceptions.CreateIOError("not modifiable string");
                }

                content.TaintBy(value);
                self._position = pos + bytesWritten;
            }
            return bytesWritten;
        }

        [RubyMethod("write")]
        [RubyMethod("syswrite")]
        public static int Write(StringIO/*!*/ self, DynamicNull value) {
            // nil.to_s is "": nothing to write, as for IO#write(nil), but the stream must be writable
            self.GetWritableContent();
            return 0;
        }

        [RubyMethod("write")]
        [RubyMethod("syswrite")]
        public static int Write(ConversionStorage<MutableString>/*!*/ tosConversion, StringIO/*!*/ self, object obj) {
            if (obj == null) {
                return Write(self, (DynamicNull)null);
            }
            return Write(self, Protocols.ConvertToString(tosConversion, obj));
        }

        /// <summary>
        /// IO#write - and so StringIO#write - takes any number of arguments and answers the
        /// total it wrote. #syswrite does not: it is the one-argument call above.
        /// </summary>
        [RubyMethod("write")]
        public static int Write(ConversionStorage<MutableString>/*!*/ tosConversion, StringIO/*!*/ self,
            object first, params object[]/*!*/ rest) {

            // A lone nil after the first argument binds as the params array itself.
            rest = rest ?? new object[] { null };
            int written = Write(tosConversion, self, first);
            foreach (object value in rest) {
                written += Write(tosConversion, self, value);
            }
            return written;
        }

        [RubyMethod("write")]
        public static int Write(StringIO/*!*/ self) {
            // Nothing to write, but the stream still has to be writable to say so.
            self.GetWritableContent();
            return 0;
        }

        #endregion

        #region read, sysread

        [RubyMethod("read")]
        public static MutableString/*!*/ Read(StringIO/*!*/ self, [Optional]DynamicNull bytes) {
            return Read(self, null, false);
        }

        [RubyMethod("read")]
        public static MutableString/*!*/ Read(StringIO/*!*/ self, DynamicNull bytes, [DefaultProtocol, NotNull]MutableString buffer) {
            return Read(self, buffer, false);
        }

        // read(nil, nil): an explicit nil buffer is the same as none (see read(n, nil) below).
        [RubyMethod("read")]
        public static MutableString/*!*/ Read(StringIO/*!*/ self, DynamicNull bytes, DynamicNull buffer) {
            return Read(self, null, false);
        }

        public static MutableString/*!*/ Read(StringIO/*!*/ self, MutableString buffer, bool eofError) {
            var content = self.GetReadableContent();
            int start = self._position;
            int length = content.GetByteCount();

            if (buffer != null) {
                buffer.Clear();
            } else {
                buffer = MutableString.CreateBinary();
            }

            if (start < length) {
                self._position = length;
                buffer.Append(content, start, length - start).TaintBy(content);
            } else if (eofError) {
                throw new EOFError("end of file reached");
            }

            // Only a read of so many bytes answers bytes; the rest of the string keeps its encoding.
            buffer.ForceEncoding(content.Encoding);
            return buffer;
        }

        /// <summary>
        /// read(n, nil). An explicit nil buffer is the same as none - MRI only refuses a buffer
        /// that is neither. Libraries pass one through from their own optional argument without
        /// looking: aws-sdk-core's Query::ParamList::IoWrapper#read is `@io.read(bytes, buffer)`,
        /// which is how every signed AWS request with a body reaches this.
        /// </summary>
        [RubyMethod("read")]
        public static MutableString Read(StringIO/*!*/ self, [DefaultProtocol]int count, DynamicNull buffer) {
            return Read(self, count, (MutableString)null);
        }

        [RubyMethod("read")]
        public static MutableString Read(StringIO/*!*/ self, [DefaultProtocol]int count, [DefaultProtocol, Optional, NotNull]MutableString buffer) {
            var content = self.GetReadableContent();
            if (count < 0) {
                throw RubyExceptions.CreateArgumentError("negative length {0} given", count);
            }

            // A read of so many bytes answers bytes: MRI tags the result ASCII-8BIT however the
            // string is encoded, and a buffer it was handed keeps the encoding it came with -
            // appending to it must not re-tag it the way appending normally would.
            RubyEncoding encoding;
            if (buffer != null) {
                encoding = buffer.Encoding;
                buffer.Clear();
            } else {
                encoding = RubyEncoding.Binary;
            }

            int length = content.GetByteCount();
            // A zero length read answers an empty string wherever the position is; only a read
            // that asked for bytes and found none answers nil.
            if (count == 0) {
                return buffer ?? MutableString.CreateBinary();
            }

            if (self._position >= length) {
                return null;
            }

            if (buffer == null) {
                buffer = MutableString.CreateBinary();
            }

            int bytesRead = Math.Min(count, length - self._position);
            buffer.Append(content, self._position, bytesRead).TaintBy(content);
            buffer.ForceEncoding(encoding);
            self._position += bytesRead;
            return buffer;
        }

        /// <summary>
        /// pread(maxlen, offset, outbuf = nil): a read at an absolute offset that leaves the
        /// position where it was. As on a real descriptor, reading at or past the end is an
        /// EOFError rather than nil - except for a zero length, which answers "".
        /// </summary>
        [RubyMethod("pread")]
        public static MutableString/*!*/ PRead(StringIO/*!*/ self, [DefaultProtocol]int count, [DefaultProtocol]int offset,
            [DefaultProtocol, Optional, NotNull]MutableString buffer) {

            self.GetReadableContent();
            if (count < 0) {
                throw RubyExceptions.CreateArgumentError(String.Format("negative string size (or size too big): {0}", count));
            }
            // A zero length read is answered before anything is looked at: MRI neither seeks
            // nor touches a given buffer, so even a negative offset is accepted here.
            if (count == 0) {
                return buffer ?? MutableString.CreateBinary();
            }
            if (offset < 0) {
                throw RubyExceptions.CreateEINVAL();
            }

            lock (self._mutex) {
                int saved = self._position;
                try {
                    self._position = offset;
                    var result = Read(self, count, buffer);
                    if (result == null) {
                        throw new EOFError("end of file reached");
                    }
                    return result;
                } finally {
                    self._position = saved;
                }
            }
        }

        // MRI's #sysread is #read with the one difference that a nil answer is an error - and
        // #read without a length never answers nil, it answers "" at the end of the stream.
        [RubyMethod("sysread")]
        public static MutableString/*!*/ SystemRead(StringIO/*!*/ self, [Optional]DynamicNull bytes) {
            return Read(self, null, false);
        }

        [RubyMethod("sysread")]
        public static MutableString/*!*/ SystemRead(StringIO/*!*/ self, DynamicNull bytes, [DefaultProtocol, NotNull]MutableString buffer) {
            return Read(self, buffer, false);
        }

        // A string is always ready, so #readpartial is #sysread: it answers the whole of what was
        // asked for when it is there, clears the buffer whether or not it succeeds, and reports
        // the end of the stream as an error - except for a zero length, which is always "".
        [RubyMethod("sysread")]
        [RubyMethod("readpartial")]
        public static MutableString/*!*/ SystemRead(StringIO/*!*/ self, [DefaultProtocol]int bytes, [DefaultProtocol, Optional, NotNull]MutableString buffer) {
            var result = Read(self, bytes, buffer);
            if (result == null) {
                throw new EOFError("end of file reached");
            }
            return result;
        }

        #endregion

        #region getc, getbyte (1.9), ungetc, ungetbyte (1.9), readchar, readbyte (1.9)

        [RubyMethod("getc")]
        public static object GetByte(StringIO/*!*/ self) {
            var content = self.GetReadableContent();

            if (self._position >= content.GetByteCount()) {
                return null;
            }

            return ScriptingRuntimeHelpers.Int32ToObject(content.GetByte(self._position++));
        }

        [RubyMethod("ungetc")]
        public static void SetPreviousByte(StringIO/*!*/ self, [DefaultProtocol]int b) {
            // MRI: this checks if the IO is readable although it actually modifies the string:
            MutableString content = self.GetReadableContent();

            int pos = self._position - 1;
            if (pos >= 0) {
                int length = content.GetByteCount();
                try {
                    if (pos >= length) {
                        content.Append(0, pos - length);
                        content.Append(unchecked((byte)b));
                    } else {
                        content.SetByte(pos, unchecked((byte)b));
                    }
                    self._position = pos;
                } catch (InvalidOperationException) {
                    throw RubyExceptions.CreateIOError("not modifiable string");
                }
            }
        }

        // returns a string in 1.9
        [RubyMethod("readchar")]
        public static int ReadChar(StringIO/*!*/ self) {
            var content = self.GetReadableContent();
            int length = content.GetByteCount();

            if (self._position >= length) {
                throw new EOFError("end of file reached");
            }

            return content.GetByte(self._position++);
        }

        #endregion

        #region gets, readline, readlines

        //
        // Note
        //
        // MRI: the behavior of IO#readline and StringIO#readline is different.
        // StringIO doesn't normalize EOLNs.
        // Also, gets/readlines increment _lineNumber field instead of global $. variable.
        // 

        [RubyMethod("lineno")]
        public static int GetLineNo(StringIO/*!*/ self) {
            return self._lineNumber;
        }

        [RubyMethod("lineno=")]
        public static void SetLineNo(StringIO/*!*/ self, [DefaultProtocol]int value) {
            self._lineNumber = value;
        }

        /// <summary>
        /// A limit of zero would read an empty line for ever, so MRI refuses it where a reader
        /// loops rather than looping - the same message IO#each_line gives.
        /// </summary>
        private static void CheckLineLimit(int limit, string/*!*/ methodName) {
            if (limit == 0) {
                throw RubyExceptions.CreateArgumentError("invalid limit: 0 for {0}", methodName);
            }
        }

        /// <summary>
        /// The argument shapes the four line readers share: (), (separator), (limit),
        /// (separator, limit), with a chomp: keyword on any of them. A lone Integer is the limit
        /// rather than the separator - the one shape that arity cannot tell apart - and a nil
        /// separator means "read everything".
        /// </summary>
        private static void ParseLineArguments(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt, RubyContext/*!*/ context,
            object first, object second, IDictionary<object, object> options,
            out MutableString separator, out int limit, out bool chomp) {

            Protocols.TryConvertToOptions(toHash, ref options, ref first, ref second);

            separator = context.InputSeparator;
            limit = -1;
            chomp = false;

            object value;
            if (options != null && options.TryGetValue(context.CreateAsciiSymbol("chomp"), out value)) {
                chomp = RubyOps.IsTrue(value);
            }

            if (second != Missing.Value) {
                separator = (first == null) ? null : Protocols.CastToString(toStr, first);
                limit = (second == null) ? -1 : Protocols.CastToFixnum(toInt, second);
            } else if (first != Missing.Value) {
                if (first == null) {
                    separator = null;
                } else if (first is int) {
                    limit = (int)first;
                } else if (first is MutableString) {
                    separator = (MutableString)first;
                } else {
                    // to_str is tried before to_int, which is the order MRI tries them in
                    MutableString asString = Protocols.TryCastToString(toStr, first);
                    if (asString != null) {
                        separator = asString;
                    } else {
                        limit = Protocols.CastToFixnum(toInt, first);
                    }
                }
            }
        }

        /// <summary>
        /// chomp takes off the separator that was actually found: a nil separator read to the end
        /// and has nothing to take off, and an empty one read a paragraph, whose separator is the
        /// run of blank lines that ended it.
        /// </summary>
        private static MutableString Chomp(MutableString line, MutableString separator, bool chomp) {
            if (!chomp || line == null || separator == null) {
                return line;
            }

            int length = line.GetByteCount();
            if (separator.IsEmpty) {
                int end = length;
                while (end > 0 && line.GetByte(end - 1) == '\n') {
                    end--;
                }
                return (end == length) ? line : line.GetSlice(0, end);
            }

            int separatorLength = separator.GetByteCount();
            if (separatorLength == 0 || length < separatorLength || !line.EndsWith(separator)) {
                return line;
            }

            int cut = length - separatorLength;
            // A line ended by "\n" gives up the "\r" in front of it too - MRI chomps the pair -
            // but only that separator does: gets(">", chomp: true) leaves a "\r" alone.
            if (separatorLength == 1 && separator.GetByte(0) == (byte)'\n' && cut > 0 && line.GetByte(cut - 1) == (byte)'\r') {
                cut--;
            }
            return line.GetSlice(0, cut);
        }

        [RubyMethod("gets")]
        public static MutableString Gets(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyScope/*!*/ scope, StringIO/*!*/ self,
            [Optional]object separatorOrLimit, [Optional]object limitOrOptions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            MutableString separator;
            int limit;
            bool chomp;
            ParseLineArguments(toHash, toStr, toInt, scope.RubyContext, separatorOrLimit, limitOrOptions, options,
                out separator, out limit, out chomp);

            var content = self.GetReadableContent();

            int position = self._position;
            MutableString result = Chomp(ReadLine(content, separator, limit, ref position), separator, chomp);
            self._position = position;

            // $_ is frame local, and the frame it belongs in is the caller's - which is why this
            // is not wrapped in Ruby the way the chomp: option once was.
            scope.GetInnerMostClosureScope().LastInputLine = result;
            self._lineNumber++;

            return result;
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyScope/*!*/ scope, StringIO/*!*/ self,
            [Optional]object separatorOrLimit, [Optional]object limitOrOptions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            // no dynamic call, modifies $_ scope variable:
            MutableString result = Gets(toHash, toStr, toInt, scope, self, separatorOrLimit, limitOrOptions, options);
            if (result == null) {
                throw new EOFError("end of file reached");
            }

            return result;
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyContext/*!*/ context, StringIO/*!*/ self,
            [Optional]object separatorOrLimit, [Optional]object limitOrOptions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            MutableString separator;
            int limit;
            bool chomp;
            ParseLineArguments(toHash, toStr, toInt, context, separatorOrLimit, limitOrOptions, options,
                out separator, out limit, out chomp);

            var content = self.GetReadableContent();
            CheckLineLimit(limit, "readlines");
            RubyArray result = new RubyArray();

            // no dynamic call, doesn't modify $_ scope variable:
            MutableString line;
            int position = self._position;
            while ((line = ReadLine(content, separator, limit, ref position)) != null) {
                result.Add(Chomp(line, separator, chomp));
                self._lineNumber++;
            }
            self._position = position;
            return result;
        }

        private static readonly byte[] ParagraphSeparator = new byte[] { (byte)'\n', (byte)'\n' };

        /// <summary>
        /// One line: up to and including the separator, or <paramref name="limit"/> bytes,
        /// whichever ends first - a negative limit means there is none. A nil separator reads to
        /// the end of the string and an empty one reads a paragraph, skipping the blank lines in
        /// front of it and ending at the blank line after it.
        /// </summary>
        private static MutableString ReadLine(MutableString/*!*/ content, MutableString separator, int limit, ref int position) {
            int length = content.GetByteCount();

            if (limit == 0) {
                // A limit of nothing answers an empty string and reads nothing, rather than nil -
                // at the end of the string as anywhere else.
                return content.GetSlice(0, 0);
            }

            if (position >= length) {
                return null;
            }

            int oldPosition = position;

            if (separator == null) {
                position = length;
            } else if (separator.IsEmpty) {
                // skip initial ends of line:
                while (oldPosition < length && content.GetByte(oldPosition) == '\n') {
                    oldPosition++;
                }

                int terminator = content.IndexOf(ParagraphSeparator, oldPosition);
                if (terminator == -1) {
                    position = length;
                } else {
                    // A paragraph ends with the whole run of blank lines that closed it, not
                    // with the first newline of that run: "a\n\n\nb" reads as "a\n\n\n".
                    position = terminator + 1;
                    while (position < length && content.GetByte(position) == '\n') {
                        position++;
                    }
                }
            } else {
                // Positions are byte offsets, so the separator is looked for as bytes too: searching
                // for it as a string switched a non-ASCII content to characters, giving a character
                // index (and a copy of the whole content on every call).
                byte[] separatorBytes = separator.ToByteArray();
                position = content.IndexOf(separatorBytes, oldPosition);
                position = (position != -1) ? position + separatorBytes.Length : length;
            }

            // The limit counts bytes from where the line started and wins when it is the shorter
            // of the two: gets(">", 2) answers "th", not "this>".
            if (limit > 0 && position - oldPosition > limit) {
                position = oldPosition + limit;
                // MRI relaxes the limit while it would split a character, by 16 bytes at most:
                // gets(1) on "é" answers "é". A UTF-8 continuation byte at the cut marks the split.
                if (content.Encoding.StrictEncoding.CodePage == 65001) {
                    for (int extra = 16; extra > 0 && position < length && (content.GetByte(position) & 0xC0) == 0x80; extra--) {
                        position++;
                    }
                }
            }

            return content.GetSlice(oldPosition, position - oldPosition);
        }

        #endregion

        #region each, each_line, each_byte, each_char (1.9)

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object EachLine(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyContext/*!*/ context, BlockParam block, StringIO/*!*/ self,
            [Optional]object separatorOrLimit, [Optional]object limitOrOptions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            MutableString separator;
            int limit;
            bool chomp;
            ParseLineArguments(toHash, toStr, toInt, context, separatorOrLimit, limitOrOptions, options,
                out separator, out limit, out chomp);

            var content = self.GetReadableContent();
            CheckLineLimit(limit, "each_line");
            if (block == null) {
                throw RubyExceptions.NoBlockGiven();
            }

            // Reading through the stream rather than over the string, so that the position moves
            // as the lines are yielded and the limit means the same thing it does to #gets.
            MutableString line;
            while ((line = ReadLine(content, separator, limit, ref self._position)) != null) {
                self._lineNumber++;

                object result;
                if (block.Yield(Chomp(line, separator, chomp), out result)) {
                    return result;
                }
            }
            return self;
        }

        [RubyMethod("each_byte")]
        public static object EachByte(BlockParam block, StringIO/*!*/ self) {
            MutableString content;
            int pos;
            while ((pos = self._position) < (content = self.GetReadableContent()).GetByteCount()) {
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }

                self._position++;

                object result;
                if (block.Yield(ScriptingRuntimeHelpers.Int32ToObject(content.GetByte(pos)), out result)) {
                    return result;
                }
            }
            return self;
        }

        #endregion
        
        #region ungetc, ungetbyte primitive

        /// <summary>
        /// What #ungetc and #ungetbyte are built on. Both write backwards over the string - the
        /// position moves back by as much as is pushed and those bytes replace what was there -
        /// which is MRI's behaviour rather than a pushback buffer of its own. Pushing back past
        /// the start moves the rest of the string along instead of dropping what does not fit.
        /// </summary>
        [RubyMethod("__ir_unget_bytes__", RubyMethodAttributes.PrivateInstance)]
        public static void UngetBytes(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ pushed) {
            // MRI asks for a readable stream even though this writes to the string:
            MutableString content = self.GetReadableContent();
            int count = pushed.GetByteCount();
            if (count == 0) {
                return;
            }

            // io.ungetc(io.string) pushes the content back over itself: the writes below would
            // then be reading from the string they are rewriting. Work from a copy in that case.
            if (ReferenceEquals(pushed, content)) {
                pushed = content.Clone();
            }

            try {
                int position = self._position;
                int length = content.GetByteCount();
                if (position < count) {
                    MutableString tail = content.GetSlice(position, length - position);
                    content.SetByteCount(0);
                    content.WriteBytes(0, pushed, 0, count);
                    if (tail != null && tail.GetByteCount() > 0) {
                        content.WriteBytes(count, tail, 0, tail.GetByteCount());
                    }
                    self._position = 0;
                } else {
                    if (position > length) {
                        content.Append(0, position - length);
                    }
                    content.WriteBytes(position - count, pushed, 0, count);
                    self._position = position - count;
                }
            } catch (InvalidOperationException) {
                throw RubyExceptions.CreateIOError("not modifiable string");
            }
        }

        #endregion

        #region external_encoding, internal_encoding, set_encoding (1.9)

        /// <summary>
        /// A StringIO's external encoding is its string's - there is no descriptor to carry one
        /// of its own - and it never transcodes, so the internal encoding is always nil.
        /// </summary>
        [RubyMethod("external_encoding")]
        public static RubyEncoding/*!*/ GetExternalEncoding(StringIO/*!*/ self) {
            return self._externalEncoding ?? self.GetContent().Encoding;
        }

        [RubyMethod("internal_encoding")]
        public static RubyEncoding GetInternalEncoding(StringIO/*!*/ self) {
            return null;
        }

        // Separate arities rather than [Optional]: an omitted [Optional]object arrives as
        // Missing.Value, which is neither nil nor null and has caught this codebase out before.
        [RubyMethod("set_encoding")]
        public static StringIO/*!*/ SetEncoding(ConversionStorage<MutableString>/*!*/ toStr, StringIO/*!*/ self, object external) {
            return SetExternalEncoding(toStr, self, external);
        }

        [RubyMethod("set_encoding")]
        public static StringIO/*!*/ SetEncoding(ConversionStorage<MutableString>/*!*/ toStr, StringIO/*!*/ self,
            object external, object @internal) {
            return SetExternalEncoding(toStr, self, external);
        }

        [RubyMethod("set_encoding")]
        public static StringIO/*!*/ SetEncoding(ConversionStorage<MutableString>/*!*/ toStr, StringIO/*!*/ self,
            object external, object @internal, object options) {
            return SetExternalEncoding(toStr, self, external);
        }

        /// <summary>
        /// The encoding belongs to the string, so this is String#force_encoding on it. There is
        /// nothing here to transcode between, which is why the internal encoding and the
        /// conversion options are accepted and then ignored, as MRI's stringio does.
        /// </summary>
        private static StringIO/*!*/ SetExternalEncoding(ConversionStorage<MutableString>/*!*/ toStr, StringIO/*!*/ self, object external) {
            MutableString content = self.GetContent();
            if (external == null) {
                return self;
            }

            RubyEncoding encoding = Protocols.ConvertToEncoding(toStr, external);
            self._externalEncoding = encoding;
            // The encoding belongs to the string, so tell the string too - unless it is frozen,
            // which MRI does not treat as a failure: the StringIO keeps the answer either way.
            if (!content.IsFrozen) {
                content.ForceEncoding(encoding);
            }
            return self;
        }

        #endregion

        #region Stubs: binmode, fcntl, fileno, pid, fsync, sync, sync=, isatty, tty?, flush

        /// <summary>
        /// MRI's StringIO#binmode declares the string to be bytes - it sets the encoding to
        /// BINARY - rather than doing nothing at all, and #set_encoding_by_bom will not read a
        /// mark until that is true.
        /// </summary>
        [RubyMethod("binmode")]
        public static StringIO/*!*/ SetBinaryMode(StringIO/*!*/ self) {
            MutableString content = self.GetContent();
            self._externalEncoding = RubyEncoding.Binary;
            if (!content.IsFrozen) {
                content.ForceEncoding(RubyEncoding.Binary);
            }
            return self;
        }

        [RubyMethod("binmode?")]
        public static bool IsBinmode(StringIO/*!*/ self) {
            return ReferenceEquals(GetExternalEncoding(self), RubyEncoding.Binary);
        }

        [RubyMethod("__readable_stream__?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsReadableStream(StringIO/*!*/ self) {
            return self._mode.CanRead();
        }

        /// <summary>
        /// A StringIO has no #inspect of its own in MRI: it shows as the plain object it is,
        /// without the string it wraps.  Left alone a CLR class prints its type name instead,
        /// which is neither that shape nor the same as #to_s.
        /// </summary>
        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(UnaryOpStorage/*!*/ inspectStorage,
            ConversionStorage<MutableString>/*!*/ tosConversion, StringIO/*!*/ self) {
            return RubyUtils.InspectObject(inspectStorage, tosConversion, self);
        }

        /// <summary>
        /// What StringIO.open does to the stream once its block has finished: MRI closes it and
        /// lets the string go, so #string answers nil rather than the text that was there.
        /// </summary>
        [RubyMethod("__ir_finalize__", RubyMethodAttributes.PrivateInstance)]
        public static void Finalize(StringIO/*!*/ self) {
            self._content = null;
            self._mode = self._mode.Close();
        }

        [RubyMethod("fcntl")]
        public static void FileControl(StringIO/*!*/ self) {
            throw new NotImplementedError();
        }

        [RubyMethod("fileno")]
        [RubyMethod("pid")]
        public static object GetDescriptor(StringIO/*!*/ self) {
            // nop
            return null;
        }

        [RubyMethod("fsync")]
        public static int FSync(StringIO/*!*/ self) {
            // nop
            return 0;
        }

        [RubyMethod("sync")]
        public static bool Sync(StringIO/*!*/ self) {
            // nop
            return true;
        }

        [RubyMethod("sync=")]
        public static bool SetSync(StringIO/*!*/ self, bool value) {
            // nop
            return value;
        }

        [RubyMethod("isatty")]
        [RubyMethod("tty?")]
        public static bool IsConsole(StringIO/*!*/ self) {
            // nop
            return false;
        }

        [RubyMethod("flush")]
        public static StringIO/*!*/ Flush(StringIO/*!*/ self) {
            // nop
            return self;
        }

        #endregion
    }
}

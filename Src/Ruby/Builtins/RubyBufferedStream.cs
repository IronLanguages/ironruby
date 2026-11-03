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
using System.Diagnostics;
using System.IO;
using IronRuby.Runtime;
using Microsoft.Scripting.Utils;
using System.Collections.Generic;

namespace IronRuby.Builtins {
    /// <summary>
    /// Not thread-safe.
    /// </summary>
    public class RubyBufferedStream : Stream {
        private readonly Stream/*!*/ _stream;

        // read buffer [...xxxxxxx...]
        //                 ^      ^
        //           read pos     stream pos
        private byte[] _buffer;
        private int _defaultBufferSize;

        // the position of the first buffered byte in buffer
        private int _bufferStart;          

        // the number of buffered bytes
        private int _bufferCount;

        // the number of bytes pushed back by ungetc:
        private int _pushedBackCount;

        private bool _pushBackPreservesPosition;

        // Whether something was written since the last flush or seek. Writes go straight to the
        // underlying stream, but MRI would still hold them in its write buffer, and that is what
        // IO#syswrite and IO#sysseek warn about.
        private bool _writePending;
        private bool _writeFlushFailed;

        private const byte CR = (byte)'\r';
        private const byte LF = (byte)'\n';

        public RubyBufferedStream(Stream/*!*/ stream)
            : this(stream, false) {
        }
        
        public RubyBufferedStream(Stream/*!*/ stream, bool pushBackPreservesPosition)
            : this(stream, pushBackPreservesPosition, 0x1000) {
        }

        public RubyBufferedStream(Stream/*!*/ stream, bool pushBackPreservesPosition, int bufferSize) {
            ContractUtils.RequiresNotNull(stream, "stream");
            ContractUtils.Requires(bufferSize > 0, "bufferSize", "Buffer size must be positive.");

            _stream = stream;
            _defaultBufferSize = bufferSize;
            _pushBackPreservesPosition = pushBackPreservesPosition;
        }

        public bool WritePending {
            get { return _writePending; }
        }

        public bool WriteFlushFailed {
            get { return _writeFlushFailed; }
        }

        public Stream/*!*/ BaseStream {
            get { return _stream; }
        }

        public bool DataBuffered {
            get { return _bufferCount > 0; }
        }

        private int LoadBuffer(int count) {
            Debug.Assert(_bufferCount + count <= (_buffer != null ? _buffer.Length : _defaultBufferSize));

            int bytesRead;
            if (_buffer == null) {
                Debug.Assert(_bufferCount == 0 && _bufferStart == 0);
                _buffer = new byte[_defaultBufferSize];
            } else if (_bufferStart + _bufferCount + count > _buffer.Length) {
                // shift left:
                Buffer.BlockCopy(_buffer, _bufferStart, _buffer, 0, _bufferCount);
                _bufferStart = 0;
            }

            bytesRead = _stream.Read(_buffer, _bufferCount, count);
            _bufferCount += bytesRead;
            return bytesRead;
        }

        private void ConsumeBuffered(int count) {
            _bufferCount -= count;
            _pushedBackCount -= Math.Min(_pushedBackCount, count);
            if (_bufferCount == 0) {
                _bufferStart = 0;
            } else {
                _bufferStart += count;
            }
        }
        
        private int ReadAheadCount {
            get { return _bufferCount - _pushedBackCount; }
        }

        public void PushBack(byte b) {
            if (_bufferStart > 0) {
                _buffer[--_bufferStart] = b;
            } else if (_buffer != null) {
                Utils.InsertAt(ref _buffer, _bufferCount, 0, b, 1);
            } else {
                _buffer = new byte[_defaultBufferSize];
                _buffer[0] = b;
            }
            _pushedBackCount++;
            _bufferCount++;
        }

        public override long Position {
            get {
                // TODO: this seems to be bug in MRI: you can read(0); ungetc(x); at the beginning of a stream, yet
                // you can't do ungetc(x) at the beginning of the stream.
                // (see http://redmine.ruby-lang.org/issues/show/1909)
                if (_pushBackPreservesPosition) {
                    // MRI counts pushed back bytes as unread, even past the start: after
                    // getc and ungetc the position is back where it was, and ungetc("xy") at
                    // the very start reports -2.
                    return _stream.Position - _bufferCount;
                } else {
                    return Math.Max(_stream.Position - _bufferCount, 0);
                }
            }
            set {
                ContractUtils.Requires(value >= 0, "value", "Value must be positive");
                Seek(value, SeekOrigin.Begin);
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                _buffer = null;
                _bufferCount = _bufferStart = _pushedBackCount = 0;
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }

        public override long Seek(long pos, SeekOrigin origin) {
            if (origin == SeekOrigin.Current) {
                if (_pushBackPreservesPosition) {
                    pos -= ReadAheadCount;
                } else {
                    origin = SeekOrigin.Begin;
                    pos += Position;
                }
            }

            // try seek first, it may fail and we shouldn't change the buffer if so:
            var result = _stream.Seek(pos, origin);

            // TODO: we might keep the buffered data if we seek within the buffered data (but not in pushed back data):
            // clear any buffer content (including ungetc):
            _bufferStart = _bufferCount = _pushedBackCount = 0;
            _writePending = false;

            return result;
        }

        private void FlushRead() {
            // Unwind cached data. Seek already reckons "current" from the position reads have
            // reached rather than from the one the underlying stream is at, so this asks to stay
            // where it is - subtracting the read-ahead here would take it off twice.
            if (ReadAheadCount > 0) {
                Seek(0, SeekOrigin.Current);
            }

            _bufferStart = _bufferCount = _pushedBackCount = 0;
        }

        public override void Write(byte[]/*!*/ buffer, int offset, int count) {
            FlushRead();
            _stream.Write(buffer, offset, count);
            _writePending = true;
        }

        public int WriteBytes(MutableString/*!*/ buffer, int offset, int count, bool preserveEndOfLines) {
            // TODO: this is not safe, we are passing an internal pointer to the byte[] content of MutableString to the Stream:
            return WriteBytes(buffer.SwitchToBytes().GetByteArrayChecked(offset, count), offset, count, preserveEndOfLines);
        }

        public int WriteBytes(byte[]/*!*/ buffer, int offset, int count, bool preserveEndOfLines) {
            ContractUtils.RequiresArrayRange(buffer.Length, offset, count, "offset", "count");
            FlushRead();
            _writePending |= count > 0;

            if (preserveEndOfLines) {
                _stream.Write(buffer, offset, count);
                return count;
            } else {
                int bytesWritten = 0;
                int i = offset;
                int end = offset + count;
                while (i < end) {
                    int j = i;
                    while (j < end && buffer[j] != LF) {
                        j++;
                    }
                    _stream.Write(buffer, i, j - i);
                    bytesWritten += j - i;

                    if (j < end) {
                        _stream.WriteByte(CR);
                        _stream.WriteByte(LF);
                        bytesWritten += 2;
                    }

                    i = j + 1;
                }

                return bytesWritten;
            }
        }

        public int PeekByte() {
            return PeekByte(0);
        }

        /// <summary>
        /// Peeks i-th byte. Assumes small <c>i</c>.
        /// </summary>
        private int PeekByte(int i) {
            Debug.Assert(i < (_buffer != null ? _buffer.Length : _defaultBufferSize));

            if (i >= _bufferCount) {
                LoadBuffer(i + 1 - _bufferCount);
            }

            // end of stream:
            if (i >= _bufferCount) {
                return -1;
            }

            return _buffer[_bufferStart + i];
        }

        private byte ReadBufferByte() {
            Debug.Assert(_bufferCount > 0);
            var result = _buffer[_bufferStart];
            ConsumeBuffered(1);
            return result;
        }

        // TODO: read in full buffer (underlying FileStream will buffer it anyways)
        public override int ReadByte() {
            return (_bufferCount > 0) ? ReadBufferByte() : _stream.ReadByte();
        }

        public override int Read(byte[]/*!*/ buffer, int offset, int count) {
            int c = Math.Min(_bufferCount, count);
            if (c > 0) {
                Buffer.BlockCopy(_buffer, _bufferStart, buffer, offset, c);
                ConsumeBuffered(c);
            }
            return c + _stream.Read(buffer, offset + c, count - c);
        }

        /// <summary>
        /// Reads <paramref name="count"/> bytes from the stream and appends them to the given <paramref name="buffer"/>.
        /// If <paramref name="count"/> is <c>Int32.MaxValue</c> the stream is read to the end.
        /// Unless <paramref name="preserveEndOfLines"/> is set the line endings in the appended data are normalized to "\n".
        /// </summary>
        public int AppendBytes(MutableString/*!*/ buffer, int count, bool preserveEndOfLines) {
            ContractUtils.RequiresNotNull(buffer, "buffer");
            ContractUtils.Requires(count >= 0, "count");

            if (count == 0) {
                return 0;
            }

            bool readAll = count == Int32.MaxValue;

            buffer.SwitchToBytes();
            int initialBufferSize = buffer.GetByteCount();
            if (preserveEndOfLines) {
                AppendRawBytes(buffer, count);
            } else {
                // allocate 3 more bytes at the end for a backstop and possible LF:
                byte[] bytes = Utils.EmptyBytes;

                int done = initialBufferSize;
                bool eof;
                do {
                    AppendRawBytes(buffer, readAll ? 1024 : count);
                    int end = buffer.GetByteCount();
                    int bytesRead = end - done;
                    if (bytesRead == 0) {
                        break;
                    }

                    eof = bytesRead < count;

                    buffer.EnsureCapacity(end + 3);
                    int byteCount;
                    bytes = buffer.GetByteArray(out byteCount);

                    if (bytes[end - 1] == CR && PeekByte(0) == LF) {
                        ReadByte();
                        bytes[end++] = LF;
                    }

                    // insert backstop:
                    bytes[end] = CR;
                    bytes[end + 1] = LF;

                    int last = IndexOfCrLf(bytes, done);
                    count -= last - done;
                    done = last;
                    while (last < end) {
                        int next = IndexOfCrLf(bytes, last + 2);
                        int chunk = next - last - 1;
                        Buffer.BlockCopy(bytes, last + 1, bytes, done, chunk);
                        done += chunk;
                        count -= chunk;
                        last = next;
                    }
                    buffer.Remove(done);
                } while (readAll || count > 0 && !eof);
            }

            if (readAll) {
                buffer.TrimExcess();
            }

            return buffer.GetByteCount() - initialBufferSize;
        }

        /// <summary>
        /// The bytes that are here already, up to count of them, and only when there are none
        /// a single read of at most count - which is what IO#readpartial is: it blocks only
        /// when there is nothing at all to hand back.
        /// </summary>
        public int AppendAvailableBytes(MutableString/*!*/ buffer, int count) {
            ContractUtils.RequiresNotNull(buffer, "buffer");
            if (count <= 0) {
                return 0;
            }

            buffer.SwitchToBytes();
            if (_bufferCount > 0) {
                int buffered = Math.Min(_bufferCount, count);
                buffer.Append(_buffer, _bufferStart, buffered);
                ConsumeBuffered(buffered);
                return buffered;
            }

            var bytes = new byte[count];
            int read = _stream.Read(bytes, 0, count);
            if (read > 0) {
                buffer.Append(bytes, 0, read);
            }
            return read;
        }

        private void AppendRawBytes(MutableString/*!*/ buffer, int count) {
            Debug.Assert(count > 0);

            int remaining = count;

            if (_bufferCount > 0) {
                int c = Math.Min(_bufferCount, count);
                buffer.Append(_buffer, _bufferStart, c);
                ConsumeBuffered(c);
                remaining -= c;
            }

            if (count == Int32.MaxValue) {
                const int chunk = 1024;

                int done = buffer.GetByteCount();
                int bytesRead;
                do {
                    buffer.Append(_stream, chunk);
                    bytesRead = buffer.GetByteCount() - done;
                    done += bytesRead;
                    // read until the stream really ends: a short read is normal on a pipe,
                    // where the child may not have written the rest yet
                } while (bytesRead > 0);
            } else {
                buffer.Append(_stream, remaining);
            }
        }

        private static int IndexOfCrLf(byte[]/*!*/ array, int i) {
            while (true) {
                if (array[i++] == CR && array[i] == LF) {
                    return i - 1;
                }
            }
        }

        public int ReadByteNormalizeEoln(bool preserveEndOfLines) {
            int first = ReadByte();
            if (first == '\r' && !preserveEndOfLines) {
                int second = PeekByte(0);
                if (second == '\n') {
                    return ReadByte();
                }
            }

            return first;
        }

        public int PeekByteNormalizeEoln(bool preserveEndOfLines) {
            int first = PeekByte(0);
            if (first == -1) {
                return -1;
            }

            if (first == '\r' && !preserveEndOfLines && PeekByte(1) == '\n') {
                return '\n';
            }

            return first;
        }

        public MutableString ReadLineOrParagraph(MutableString separator, RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit) {
            ContractUtils.Requires(limit >= 0);

            if (limit == 0) {
                return MutableString.CreateEmpty();
            } else if (separator == null) {
                // The whole of what is left, in the stream's own encoding rather than as bytes.
                var result = MutableString.CreateBinary(encoding);
                if (AppendBytes(result, limit, preserveEndOfLines) == 0) {
                    return null;
                }
                if (limit != Int32.MaxValue) {
                    // A byte limit is relaxed while the last character is incomplete, by 16 bytes
                    // at most (extra_limit in io.c): gets(nil, 1) on "é" answers "é", not "\xC3".
                    for (int extra = 16; extra > 0 && EndsWithIncompleteCharacter(encoding, result); extra--) {
                        if (AppendBytes(result, 1, preserveEndOfLines) == 0) {
                            break;
                        }
                    }
                }
                return result;
            } else if (separator.StartsWith('\n') && separator.GetLength() == 1) {
                // The buffer-scanning reader has no way to stop part way through a line, so a
                // read with a byte limit goes through the general one.
                return limit == Int32.MaxValue
                    ? ReadLine(encoding, preserveEndOfLines, limit)
                    : ReadLine(separator, encoding, preserveEndOfLines, limit);
            } else if (separator.IsEmpty) {
                return ReadParagraph(encoding, preserveEndOfLines, limit);
            } else {
                return ReadLine(separator, encoding, preserveEndOfLines, limit);
            }
        }

        public MutableString ReadLine(RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit) {
            // TODO: limit

            if (_bufferCount == 0) {
                if (LoadBuffer(_defaultBufferSize) == 0) {
                    return null;
                }
            }

            bool bufferResized = false;
            int lf = Array.IndexOf(_buffer, LF, _bufferStart, _bufferCount);
            while (lf < 0) {
                int s = _bufferCount;
                LoadBuffer(_buffer.Length - _bufferCount);
                Debug.Assert(_bufferStart == 0);

                lf = Array.IndexOf(_buffer, LF, s, _bufferCount - s);
                if (lf >= 0) {
                    break;
                }

                // end of stream:
                if (_bufferCount < _buffer.Length) {
                    return ConsumeLine(encoding, _bufferCount, _bufferCount, bufferResized);
                }

                Array.Resize(ref _buffer, _buffer.Length << 1);
                bufferResized = true;
                _bufferStart = 0;
            }

            int lineLength;
            int consume = lf + 1 - _bufferStart;
            if (!preserveEndOfLines && lf - 1 >= _bufferStart && _buffer[lf - 1] == CR) {
                _buffer[lf - 1] = LF;
                lineLength = consume - 1;
            } else {
                lineLength = consume;
            }

            return ConsumeLine(encoding, lineLength, consume, bufferResized);
        }

        private MutableString/*!*/ ConsumeLine(RubyEncoding/*!*/ encoding, int lineLength, int consume, bool bufferResized) {
            Debug.Assert(consume >= lineLength);
            Debug.Assert(consume <= _bufferCount);

            MutableString line;
            if (bufferResized || _bufferStart == 0 && !Utils.IsSparse(lineLength, _buffer.Length)) {
                Debug.Assert(_bufferStart == 0);
                line = new MutableString(_buffer, lineLength, encoding);

                if (_bufferCount > consume) {
                    var newBuffer = new byte[Math.Max(_defaultBufferSize, _bufferCount - consume)];
                    Buffer.BlockCopy(_buffer, consume, newBuffer, 0, _bufferCount - consume);
                    _buffer = newBuffer;
                } else {
                    _buffer = null;
                }

                // consume as if we kept the same buffer and then adjust start:
                ConsumeBuffered(consume);
                _bufferStart = 0;
            } else {
                line = MutableString.CreateBinary(encoding).Append(_buffer, _bufferStart, lineLength);
                ConsumeBuffered(consume);
            }
            return line;
        }

        public MutableString ReadParagraph(RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit) {
            // A paragraph starts at its first line that is not empty: MRI swallows the newlines
            // before it as well as the ones after it.
            SkipNewlines(preserveEndOfLines);
            var result = ReadLine(MutableString.CreateAscii("\n\n"), encoding, preserveEndOfLines, limit);
            SkipNewlines(preserveEndOfLines);
            return result;
        }

        private void SkipNewlines(bool preserveEndOfLines) {
            int c;
            while ((c = PeekByteNormalizeEoln(preserveEndOfLines)) != -1) {
                if (c != '\n') {
                    break;
                }
                ReadByteNormalizeEoln(preserveEndOfLines);
            }
        }

        /// <summary>
        /// Whether the bytes end part way through a character, so that a byte limit that lands
        /// there has to be stretched to the end of that character - which is what MRI does, and
        /// why a limit of 2 can return three bytes.
        /// </summary>
        private static bool EndsWithIncompleteCharacter(RubyEncoding/*!*/ encoding, MutableString/*!*/ str) {
            // Only UTF-8 is self-synchronising in a way we can read off the bytes; in a
            // single-byte encoding every byte is a character of its own anyway.
            if (encoding.StrictEncoding.CodePage != 65001) {
                return false;
            }

            int count = str.GetByteCount();
            int head = count - 1;
            while (head >= 0 && count - head < 4 && (str.GetByte(head) & 0xC0) == 0x80) {
                head--;
            }
            if (head < 0) {
                return false;
            }

            int lead = str.GetByte(head);
            int length = (lead >= 0xC2 && lead <= 0xDF) ? 2 : (lead >= 0xE0 && lead <= 0xEF) ? 3 : (lead >= 0xF0 && lead <= 0xF4) ? 4 : 1;
            return count - head < length;
        }

        public MutableString ReadLine(MutableString/*!*/ separator, RubyEncoding/*!*/ encoding, bool preserveEndOfLines, int limit) {
            int b = ReadByteNormalizeEoln(preserveEndOfLines);
            if (b == -1) {
                return null;
            }

            int separatorOffset = 0;
            int separatorLength = separator.GetByteCount();
            int extraLimit = 16;
            MutableString result = MutableString.CreateBinary(encoding);

            do {
                result.Append((byte)b);

                if (b == separator.GetByte(separatorOffset)) {
                    if (separatorOffset == separatorLength - 1) {
                        break;
                    }
                    separatorOffset++;
                } else if (separatorOffset > 0) {
                    separatorOffset = 0;
                }

                if (result.GetByteCount() >= limit) {
                    // MRI relaxes the limit a byte at a time while the last character is
                    // incomplete, but by 16 bytes at most (extra_limit in io.c).
                    if (extraLimit == 0 || !EndsWithIncompleteCharacter(encoding, result)) {
                        break;
                    }
                    extraLimit--;
                }

                b = ReadByteNormalizeEoln(preserveEndOfLines);
            } while (b != -1);

            return result;
        }

        public override bool CanRead {
            get { return _stream.CanRead; }
        }

        public override bool CanSeek {
            get { return _stream.CanSeek; }
        }

        public override bool CanWrite {
            get { return _stream.CanWrite; }
        }

        public override void Flush() {
            FlushRead();
            try {
                _stream.Flush();
                _writePending = false;
                _writeFlushFailed = false;
            } catch {
                _writeFlushFailed = true;
                throw;
            }
        }

        public override long Length {
            get { return _stream.Length; }
        }

        public override void SetLength(long value) {
            _stream.SetLength(value);
        }
    }
}

/* ****************************************************************************
 *
 * IronRuby's implementation of the msgpack gem's C extension: the byte buffer.
 *
 * A port of ext/msgpack/buffer.c and buffer_class.c from msgpack-ruby 1.8.5.  The
 * buffer is a list of chunks, as upstream's is, because that list is observable:
 * Buffer#to_a answers one string per chunk, and a buffer with an IO flushes to it one
 * chunk at a time.  The chunk sizes follow upstream's allocator - 4 KiB pages
 * (MSGPACK_RMEM_PAGE_SIZE), the unused tail of a page reclaimed after a reference chunk,
 * larger chunks grown by doubling - so both come out the same.  What upstream does to
 * avoid copying (mapping a long String's memory straight into a chunk) is done here with
 * a private copy of the bytes: the String cannot change under it either way.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.MessagePack {

    internal sealed class Chunk {
        internal byte[] Mem;          // null: an empty chunk
        internal int First;           // first byte of the chunk in Mem
        internal int Last;            // one past its last byte
        internal MutableString Mapped; // non-null: a reference chunk (a long String written by reference)

        internal int Length { get { return Last - First; } }
    }

    /// <summary>
    /// msgpack_buffer_t.
    /// </summary>
    internal sealed class BufferCore {
        internal const int PageSize = 4 * 1024;                  // MSGPACK_RMEM_PAGE_SIZE
        internal const int WriteReferenceDefault = 512 * 1024;   // MSGPACK_BUFFER_STRING_WRITE_REFERENCE_DEFAULT
        internal const int WriteReferenceMinimum = 256;
        internal const int ReadReferenceDefault = 256;
        internal const int ReadReferenceMinimum = 256;
        internal const int IoBufferSizeDefault = 32 * 1024;
        internal const int IoBufferSizeMinimum = 1024;

        private readonly RubyContext/*!*/ _context;
        private readonly List<Chunk>/*!*/ _chunks = new List<Chunk>();   // [0] is the head, [^1] the tail
        private int _readPos;       // read position in the head chunk
        private int _tailEnd;       // end of the tail chunk's writable space

        // the rmem page the next small chunk is carved from
        private byte[] _rmemPage;
        private int _rmemLast;
        private int _rmemEnd;
        private Chunk _rmemOwner;

        internal object Io;
        internal string IoPartialReadMethod = "read";
        internal string IoWriteAllMethod = "write";
        private MutableString _ioBuffer;

        internal int WriteReferenceThreshold = WriteReferenceDefault;
        internal int ReadReferenceThreshold = ReadReferenceDefault;
        internal int IoBufferSize = IoBufferSizeDefault;

        internal BufferCore(RubyContext/*!*/ context) {
            _context = context;
            _tail = new Chunk();
            _chunks.Add(_tail);
        }

        internal RubyContext/*!*/ Context { get { return _context; } }

        private Chunk/*!*/ Head { get { return _chunks[0]; } }
        private Chunk/*!*/ _tail;   // == _chunks[_chunks.Count - 1], kept at hand for the writers
        private Chunk/*!*/ Tail { get { return _tail; } }
        private bool HeadIsTail { get { return _chunks.Count == 1; } }

        internal bool HasIo { get { return Io != null; } }

        #region options

        /// <summary>MessagePack_Buffer_set_options.</summary>
        internal void SetOptions(object io, Hash options) {
            Io = io;
            IoPartialReadMethod = (io != null && Sites.RespondTo(_context, io, "readpartial")) ? "readpartial" : "read";
            if (io != null && !Sites.RespondTo(_context, io, "write") && Sites.RespondTo(_context, io, "<<")) {
                IoWriteAllMethod = "<<";
            } else {
                IoWriteAllMethod = "write";
            }

            if (options != null) {
                object v;
                if ((v = Sites.Option(_context, options, "read_reference_threshold")) != null) {
                    ReadReferenceThreshold = Math.Max(Sites.ToSize(_context, v), ReadReferenceMinimum);
                }
                if ((v = Sites.Option(_context, options, "write_reference_threshold")) != null) {
                    WriteReferenceThreshold = Math.Max(Sites.ToSize(_context, v), WriteReferenceMinimum);
                }
                if ((v = Sites.Option(_context, options, "io_buffer_size")) != null) {
                    IoBufferSize = Math.Max(Sites.ToSize(_context, v), IoBufferSizeMinimum);
                }
            }
        }

        #endregion

        #region writing

        private int WritableSize {
            get {
                Chunk tail = Tail;
                return (tail.Mem == null || tail.Mapped != null) ? 0 : _tailEnd - tail.Last;
            }
        }

        /// <summary>_msgpack_buffer_add_new_chunk: the tail becomes an ordinary chunk and a fresh tail follows it.</summary>
        private void AddNewChunk() {
            Chunk tail = Tail;
            if (HeadIsTail) {
                if (tail.Mem == null) {
                    // empty buffer: the tail itself is rebuilt
                    return;
                }
            } else if (_rmemPage != null && ReferenceEquals(tail.Mem, _rmemPage) && _rmemLast == _tailEnd) {
                // reuse the unused part of the page
                _rmemLast -= _tailEnd - tail.Last;
            }
            _tail = new Chunk();
            _chunks.Add(_tail);
        }

        /// <summary>_msgpack_buffer_chunk_malloc: gives the chunk memory, answers where its bytes start.</summary>
        private int ChunkMalloc(Chunk/*!*/ c, int requiredSize, out int allocatedSize) {
            if (requiredSize <= PageSize) {
                if (_rmemPage == null || _rmemEnd - _rmemLast < requiredSize) {
                    byte[] page = new byte[PageSize];
                    c.Mem = page;
                    _rmemPage = page;
                    _rmemOwner = c;
                    _rmemLast = _rmemEnd = PageSize;
                    allocatedSize = PageSize;
                    return 0;
                }
                allocatedSize = _rmemEnd - _rmemLast;
                int start = _rmemLast;
                _rmemLast = _rmemEnd;
                c.Mem = _rmemPage;
                _rmemOwner = c;
                return start;
            }

            allocatedSize = requiredSize;
            c.Mem = new byte[requiredSize];
            return 0;
        }

        /// <summary>_msgpack_buffer_expand.  data == null means "make room for length bytes".</summary>
        private void Expand(byte[] data, int offset, int length, bool flushToIo) {
            if (flushToIo && Io != null) {
                Flush();
                if (WritableSize >= length) {
                    if (data != null) {
                        Chunk t = Tail;
                        Buffer.BlockCopy(data, offset, t.Mem, t.Last, length);
                        t.Last += length;
                    }
                    return;
                }
            }

            if (data != null) {
                int tailAvail = WritableSize;
                if (tailAvail > 0) {
                    Chunk t = Tail;
                    Buffer.BlockCopy(data, offset, t.Mem, t.Last, tailAvail);
                    t.Last += tailAvail;
                    offset += tailAvail;
                    length -= tailAvail;
                }
            }

            Chunk tail = Tail;
            int capacity = tail.Last - tail.First;

            if (tail.Mapped != null || capacity <= PageSize) {
                AddNewChunk();
                tail = Tail;

                int allocated;
                int start = ChunkMalloc(tail, length, out allocated);
                tail.First = start;
                tail.Last = start;
                if (data != null) {
                    Buffer.BlockCopy(data, offset, tail.Mem, start, length);
                    tail.Last += length;
                }
                tail.Mapped = null;
                _tailEnd = start + allocated;

                if (HeadIsTail) {
                    _readPos = tail.First;
                }
            } else {
                // grow a malloc()ed chunk: upstream doubles the filled size until it fits
                int filled = tail.Last - tail.First;
                long nextSize = (long)capacity * 2;
                while (nextSize < filled + length) {
                    nextSize *= 2;
                }
                if (nextSize > Int32.MaxValue) {
                    nextSize = Math.Max(filled + length, Int32.MaxValue - 64);
                }
                byte[] mem = new byte[nextSize];
                Buffer.BlockCopy(tail.Mem, tail.First, mem, 0, filled);
                if (HeadIsTail) {
                    _readPos -= tail.First;
                }
                tail.Mem = mem;
                tail.First = 0;
                tail.Last = filled;
                if (data != null) {
                    Buffer.BlockCopy(data, offset, mem, filled, length);
                    tail.Last += length;
                }
                _tailEnd = (int)nextSize;
            }
        }

        internal void EnsureWritable(int require) {
            if (WritableSize < require) {
                Expand(null, 0, require, true);
            }
        }

        /// <summary>Makes room for length bytes at the end and hands them out: tail.Mem[offset..offset+length).</summary>
        internal byte[]/*!*/ Reserve(int length, out int offset) {
            Chunk tail = _tail;
            if (tail.Mem == null || tail.Mapped != null || _tailEnd - tail.Last < length) {
                Expand(null, 0, length, true);
                tail = _tail;
            }
            offset = tail.Last;
            tail.Last += length;
            return tail.Mem;
        }

        /// <summary>Writes bytes the caller has made room for with EnsureWritable.</summary>
        internal void WriteByte(byte b) {
            Chunk tail = Tail;
            tail.Mem[tail.Last++] = b;
        }

        internal void WriteBytesUnchecked(byte[]/*!*/ data, int offset, int length) {
            Chunk tail = Tail;
            Buffer.BlockCopy(data, offset, tail.Mem, tail.Last, length);
            tail.Last += length;
        }

        internal void Append(byte[]/*!*/ data, int offset, int length, bool flushToIo) {
            if (length == 0) {
                return;
            }
            if (length <= WritableSize) {
                WriteBytesUnchecked(data, offset, length);
                return;
            }
            Expand(data, offset, length, flushToIo);
        }

        /// <summary>msgpack_buffer_append_string.</summary>
        internal int AppendString(MutableString/*!*/ str) {
            return AppendString(str, str.ToByteArray());
        }

        /// <summary>The same, with the String's bytes already in hand.</summary>
        internal int AppendString(MutableString/*!*/ str, byte[]/*!*/ bytes) {
            if (bytes.Length > WriteReferenceThreshold) {
                AppendLongString(str, bytes);
            } else {
                Append(bytes, 0, bytes.Length, true);
            }
            return bytes.Length;
        }

        /// <summary>msgpack_buffer_append_string_reference (Unpacker#feed).</summary>
        internal int AppendStringReference(MutableString/*!*/ str) {
            byte[] bytes = str.ToByteArray();
            if (bytes.Length > 0) {
                AppendLongString(str, bytes);
            }
            return bytes.Length;
        }

        private void AppendLongString(MutableString/*!*/ str, byte[]/*!*/ bytes) {
            if (Io != null) {
                Flush();
                if (str.Encoding == RubyEncoding.Binary) {
                    Sites.Call(_context, Io, IoWriteAllMethod, str);
                } else {
                    Append(bytes, 0, bytes.Length, true);
                }
            } else {
                AppendReference(str, bytes);
            }
        }

        private void AppendReference(MutableString/*!*/ str, byte[]/*!*/ bytes) {
            MutableString mapped = (str.Encoding == RubyEncoding.Binary && str.IsFrozen)
                ? str
                : MutableString.CreateBinary(bytes, RubyEncoding.Binary);

            AddNewChunk();
            Chunk tail = Tail;
            tail.Mem = bytes;
            tail.First = 0;
            tail.Last = bytes.Length;
            tail.Mapped = mapped;
            _tailEnd = tail.Last;

            if (HeadIsTail) {
                _readPos = tail.First;
            }
        }

        #endregion

        #region reading

        internal int TopReadableSize {
            get {
                Chunk head = Head;
                return head.Mem == null ? 0 : head.Last - _readPos;
            }
        }

        internal long AllReadableSize {
            get {
                long size = TopReadableSize;
                for (int i = 1; i < _chunks.Count; i++) {
                    size += _chunks[i].Length;
                }
                return size;
            }
        }

        /// <summary>_msgpack_buffer_shift_chunk.</summary>
        private bool ShiftChunk() {
            Chunk head = Head;
            if (ReferenceEquals(_rmemOwner, head)) {
                // the page goes away with the chunk that owns it
                _rmemPage = null;
                _rmemLast = _rmemEnd = 0;
                _rmemOwner = null;
            }

            if (HeadIsTail) {
                head.Mem = null;
                head.First = head.Last = 0;
                head.Mapped = null;
                _tailEnd = 0;
                _readPos = 0;
                _rmemPage = null;
                _rmemLast = _rmemEnd = 0;
                _rmemOwner = null;
                return false;
            }

            _chunks.RemoveAt(0);
            _readPos = Head.First;
            return true;
        }

        internal void Clear() {
            while (ShiftChunk()) {
            }
        }

        private void Consumed(int length) {
            _readPos += length;
            if (_readPos >= Head.Last) {
                ShiftChunk();
            }
        }

        /// <summary>msgpack_buffer_read_1: -1 at the end of a buffer without an IO.</summary>
        internal int ReadByte() {
            if (TopReadableSize <= 0) {
                if (Io == null) {
                    return -1;
                }
                FeedFromIo();
            }
            int r = Head.Mem[_readPos];
            Consumed(1);
            return r;
        }

        internal bool EnsureReadable(long require) {
            if (TopReadableSize < require) {
                long size = AllReadableSize;
                if (size < require) {
                    if (Io == null) {
                        return false;
                    }
                    do {
                        size += FeedFromIo();
                    } while (size < require);
                }
            }
            return true;
        }

        /// <summary>msgpack_buffer_read_all.</summary>
        internal bool ReadAll(byte[]/*!*/ buffer, int length) {
            if (TopReadableSize < length) {
                if (!EnsureReadable(length)) {
                    return false;
                }
                ReadNonblock(buffer, 0, length);
                return true;
            }
            Buffer.BlockCopy(Head.Mem, _readPos, buffer, 0, length);
            Consumed(length);
            return true;
        }

        /// <summary>msgpack_buffer_read_nonblock; buffer == null skips.</summary>
        internal long ReadNonblock(byte[] buffer, int offset, long length) {
            long lengthOrig = length;
            while (true) {
                int avail = TopReadableSize;
                if (length <= avail) {
                    if (buffer != null) {
                        Buffer.BlockCopy(Head.Mem, _readPos, buffer, offset, (int)length);
                    }
                    Consumed((int)length);
                    return lengthOrig;
                }
                if (buffer != null && avail > 0) {
                    Buffer.BlockCopy(Head.Mem, _readPos, buffer, offset, avail);
                    offset += avail;
                }
                length -= avail;
                if (!ShiftChunk()) {
                    return lengthOrig - length;
                }
            }
        }

        internal long SkipNonblock(long length) {
            int avail = TopReadableSize;
            if (avail < length) {
                return ReadNonblock(null, 0, length);
            }
            Consumed((int)length);
            return length;
        }

        /// <summary>msgpack_buffer_read_to_string_nonblock.</summary>
        internal long ReadToStringNonblock(MutableString/*!*/ str, long length) {
            long lengthOrig = length;
            int avail = TopReadableSize;
            while (true) {
                if (length <= avail) {
                    str.Append(Head.Mem, _readPos, (int)length);
                    Consumed((int)length);
                    return lengthOrig;
                }
                if (avail > 0) {
                    str.Append(Head.Mem, _readPos, avail);
                }
                length -= avail;
                if (!ShiftChunk()) {
                    return lengthOrig - length;
                }
                avail = TopReadableSize;
            }
        }

        internal long ReadToString(MutableString/*!*/ str, long length) {
            if (length == 0) {
                return 0;
            }
            if (TopReadableSize > 0) {
                return ReadToStringNonblock(str, length);
            } else if (Io != null) {
                return ReadFromIoToString(str, length);
            }
            return 0;
        }

        internal long Skip(long length) {
            if (length == 0) {
                return 0;
            }
            if (TopReadableSize > 0) {
                return SkipNonblock(length);
            } else if (Io != null) {
                return SkipFromIo(length);
            }
            return 0;
        }

        /// <summary>The first length bytes of the head chunk, which the caller knows are there, as a String.</summary>
        internal MutableString/*!*/ ReadTopAsString(int length, RubyEncoding/*!*/ encoding) {
            var result = MutableString.CreateBinary(length, encoding);
            if (length > 0) {
                result.Append(Head.Mem, _readPos, length);
                Consumed(length);
            }
            return result;
        }

        #endregion

        /// <summary>msgpack_buffer_memsize: the chunk headers, plus the Strings written by reference.</summary>
        internal long MemorySize {
            get {
                long size = 0;
                foreach (Chunk c in _chunks) {
                    size += 48;
                    if (c.Mapped != null) {
                        size += c.Length;
                    }
                }
                return size;
            }
        }

        #region as strings

        internal byte[]/*!*/ AllAsBytes() {
            long size = AllReadableSize;
            byte[] result = new byte[size];
            int top = TopReadableSize;
            if (top > 0) {
                Buffer.BlockCopy(Head.Mem, _readPos, result, 0, top);
            }
            int offset = top;
            for (int i = 1; i < _chunks.Count; i++) {
                Chunk c = _chunks[i];
                Buffer.BlockCopy(c.Mem, c.First, result, offset, c.Length);
                offset += c.Length;
            }
            return result;
        }

        internal MutableString/*!*/ AllAsString() {
            return MutableString.CreateBinary(AllAsBytes(), RubyEncoding.Binary);
        }

        private MutableString/*!*/ HeadChunkAsString() {
            int length = TopReadableSize;
            var result = MutableString.CreateBinary(length, RubyEncoding.Binary);
            if (length > 0) {
                result.Append(Head.Mem, _readPos, length);
            }
            return result;
        }

        private static MutableString/*!*/ ChunkAsString(Chunk/*!*/ c) {
            var result = MutableString.CreateBinary(c.Length, RubyEncoding.Binary);
            if (c.Length > 0) {
                result.Append(c.Mem, c.First, c.Length);
            }
            return result;
        }

        internal RubyArray/*!*/ AllAsStringArray() {
            var result = new RubyArray();
            result.Add(HeadChunkAsString());
            for (int i = 1; i < _chunks.Count; i++) {
                result.Add(ChunkAsString(_chunks[i]));
            }
            return result;
        }

        #endregion

        #region IO

        internal long FlushToIo(object io, string/*!*/ writeMethod, bool consume) {
            if (TopReadableSize == 0) {
                return 0;
            }

            MutableString s = HeadChunkAsString();
            Sites.Call(_context, io, writeMethod, s);
            long size = s.GetByteCount();

            if (consume) {
                while (ShiftChunk()) {
                    s = ChunkAsString(Head);
                    Sites.Call(_context, io, writeMethod, s);
                    size += s.GetByteCount();
                }
                return size;
            }

            for (int i = 1; i < _chunks.Count; i++) {
                s = ChunkAsString(_chunks[i]);
                Sites.Call(_context, io, writeMethod, s);
                size += s.GetByteCount();
            }
            return size;
        }

        internal long Flush() {
            if (Io == null) {
                return 0;
            }
            return FlushToIo(Io, IoWriteAllMethod, true);
        }

        /// <summary>_msgpack_buffer_feed_from_io.</summary>
        private int FeedFromIo() {
            if (_ioBuffer == null) {
                object result = Sites.Call(_context, Io, IoPartialReadMethod, IoBufferSize);
                if (result == null) {
                    throw new EOFError("IO reached end of file");
                }
                _ioBuffer = Sites.StringValue(_context, result);
            } else {
                object result = Sites.Call(_context, Io, IoPartialReadMethod, IoBufferSize, _ioBuffer);
                if (result == null) {
                    throw new EOFError("IO reached end of file");
                }
            }

            byte[] bytes = _ioBuffer.ToByteArray();
            if (bytes.Length == 0) {
                throw new EOFError("IO reached end of file");
            }
            Append(bytes, 0, bytes.Length, false);
            return bytes.Length;
        }

        private long ReadFromIoToString(MutableString/*!*/ str, long length) {
            int request = (int)Math.Min(IoBufferSize, length);
            if (str.GetByteCount() == 0) {
                // direct read
                object ret = Sites.Call(_context, Io, IoPartialReadMethod, request, str);
                if (ret == null) {
                    return 0;
                }
                return str.GetByteCount();
            }

            if (_ioBuffer == null) {
                _ioBuffer = MutableString.CreateBinary();
            }
            object r = Sites.Call(_context, Io, IoPartialReadMethod, request, _ioBuffer);
            if (r == null) {
                return 0;
            }
            byte[] bytes = _ioBuffer.ToByteArray();
            str.Append(bytes, 0, bytes.Length);
            return bytes.Length;
        }

        private long SkipFromIo(long length) {
            if (_ioBuffer == null) {
                _ioBuffer = MutableString.CreateBinary();
            }
            object ret = Sites.Call(_context, Io, IoPartialReadMethod, (int)Math.Min(length, Int32.MaxValue), _ioBuffer);
            if (ret == null) {
                return 0;
            }
            return _ioBuffer.GetByteCount();
        }

        #endregion
    }
}

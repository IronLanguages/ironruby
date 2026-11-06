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
using System.IO;
using System.Threading;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using System.Text;
using System.Diagnostics;

namespace IronRuby.Builtins {
    // TODO: remove (RubyIO class should handle this functionality)
    internal sealed class DuplexStream : Stream {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public DuplexStream(StreamReader reader, StreamWriter writer) {
            Debug.Assert(reader != null || writer != null);
            _reader = reader;
            _writer = writer;
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (_reader != null) {
                    _reader.Dispose();
                }
                if (_writer != null) {
                    try {
                        _writer.Dispose();
                    } catch (IOException) {
                        // Closing the writable half of a bidirectional popen after the child has
                        // exited commonly observes EPIPE. Any pending Ruby write was flushed by
                        // IO#close before disposal and reported there; disposal itself is silent.
                    }
                }
            }

            base.Dispose(disposing);
        }

        public StreamReader Reader {
            get { return _reader; }
        }

        public StreamWriter Writer {
            get { return _writer; }
        }

        public override bool CanRead {
            get { return _reader != null; }
        }

        public override bool CanSeek {
            get { return false; }
        }

        public override bool CanWrite {
            get { return _writer != null; }
        }

        public override void Flush() {
            if (_writer != null) {
                _writer.Flush();
            }
        }

        public override long Length {
            get { throw new NotSupportedException(); }
        }

        public override long Position {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        // A Ruby IO reads and writes bytes: they go straight to and from the pipes under the reader
        // and the writer, which only own the pipes. Going through the writer's text encoding would
        // turn any byte not valid in it into an exception or a replacement character.

        public override int ReadByte() {
            return ReadStream.ReadByte();
        }

        public override int Read(byte[]/*!*/ buffer, int offset, int count) {
            return ReadStream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        public override void Write(byte[]/*!*/ buffer, int offset, int count) {
            if (_writer == null) {
                throw new InvalidOperationException();
            }

            // whatever text the writer still holds goes first; IO.popen's write end is synchronous, as in MRI
            _writer.Flush();
            _writer.BaseStream.Write(buffer, offset, count);
            _writer.BaseStream.Flush();
        }

        private Stream/*!*/ ReadStream {
            get {
                if (_reader == null) {
                    throw new InvalidOperationException();
                }
                return _reader.BaseStream;
            }
        }
    }
}

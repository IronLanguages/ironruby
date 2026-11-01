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

namespace IronRuby.Builtins {
    /// <summary>
    /// A custom Stream class that forwards calls to Console In/Out/Error based on ConsoleType
    /// </summary>
    internal sealed class ConsoleStream : Stream {
        private ConsoleStreamType _consoleType;
        private readonly SharedIO _io;

        public ConsoleStream(SharedIO/*!*/ io, ConsoleStreamType consoleType) {
            Assert.NotNull(io);
            _consoleType = consoleType;
            _io = io;
        }

        public ConsoleStreamType StreamType {
            get { return _consoleType; }
        }

        public override bool CanRead {
            get { return _consoleType == ConsoleStreamType.Input ? true : false; }
        }

        public override bool CanSeek {
            get { return false; }
        }

        public override bool CanWrite {
            get { return _consoleType == ConsoleStreamType.Input ? false : true; }
        }

        public override void Flush() {
            switch (_consoleType) {
                case ConsoleStreamType.ErrorOutput:
                    _io.ErrorWriter.Flush();
                    break;

                case ConsoleStreamType.Output:
                    _io.OutputWriter.Flush();
                    break;

                case ConsoleStreamType.Input:
                    throw new NotSupportedException();
            }
        }

        public override long Length {
            get { throw new NotSupportedException(); }
        }

        public override long Position {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[]/*!*/ buffer, int offset, int count) {
            var raw = GetUnbufferedInput();
            return (raw ?? _io.InputStream).Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        public override void Write(byte[]/*!*/ buffer, int offset, int count) {
            bool output = _consoleType == ConsoleStreamType.Output;
            Stream stream = output ? _io.OutputStream : _io.ErrorStream;
            (GetUnbufferedStream(stream, output) ?? stream).Write(buffer, offset, count);
        }

        private static Stream _rawInput;
        private static bool _rawInputChecked;

        /// <summary>
        /// On Unix .NET reconfigures the terminal around every read from its own standard input
        /// stream - it turns ICANON and ECHO back on, because that is the mode Console.ReadLine
        /// wants - so a descriptor that io/console has just put into raw mode is cooked again by
        /// the first read, and a reader waiting for one keystroke waits for a whole line instead.
        /// That is the difference between irb's line editor working and hanging on its first
        /// cursor-position query.  Reading the descriptor directly leaves the terminal alone, and
        /// returns what is there rather than filling a 4K buffer first.
        ///
        /// Only for a real terminal: with standard input redirected (a pipe, a file, the spec
        /// runner) .NET does not touch any terminal and its own stream stays in use.
        /// </summary>
        private static Stream GetUnbufferedInput() {
            if (_rawInputChecked) {
                return _rawInput;
            }

            _rawInputChecked = true;
            if (Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX) {
                return null;
            }

            try {
                if (Console.IsInputRedirected) {
                    return null;
                }
                _rawInput = new DescriptorStream(0, true, false, false);
            } catch (Exception) {
                _rawInput = null;
            }
            return _rawInput;
        }

        private static Stream _rawOutput, _rawError;

        /// <summary>
        /// Forgets what was found out about descriptors 0, 1 and 2: a warm-start worker
        /// (Hosting/WarmStart.cs) has just put different ones there.
        /// </summary>
        internal static void ResetStandardDescriptors() {
            _rawInput = null;
            _rawInputChecked = false;
            _rawOutput = null;
            _rawError = null;
        }

        /// <summary>
        /// On Unix .NET's own standard-output stream emulates SIG_IGN for SIGPIPE by swallowing EPIPE
        /// outright, so `ruby -e 'loop { puts :ok }' | head -1` never notices that its reader is gone
        /// and spins forever instead of dying the way CRuby does.  A FileStream over the very same
        /// descriptor does report the error, so use one whenever the standard stream is still the one
        /// .NET opened (an embedder that redirected SharedIO keeps its own stream) and is redirected.
        /// </summary>
        private static Stream GetUnbufferedStream(Stream/*!*/ stream, bool output) {
            if (Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX) {
                return null;
            }

            var name = stream.GetType().FullName;
            if (name == null || !name.EndsWith("ConsoleStream", StringComparison.Ordinal)) {
                return null;
            }

            ref Stream cache = ref (output ? ref _rawOutput : ref _rawError);
            if (cache == null) {
                try {
                    if (output ? !Console.IsOutputRedirected : !Console.IsErrorRedirected) {
                        return null;
                    }
                    // Sequential write(2) rather than a FileStream: when standard output and
                    // standard error have both been redirected to the same file they share one
                    // open file description, and FileStream's positional writes would have each
                    // of them overwrite the other instead of appending.
                    cache = new DescriptorStream(output ? 1 : 2, false, true, false);
                } catch (Exception) {
                    return null;
                }
            }
            return cache;
        }
    }
}

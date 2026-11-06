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
                    // nothing to flush on the way in; IO#sysread flushes whatever stream it reads
                    break;
            }
        }

        /// <summary>The descriptor stream standard input is read through on a Unix terminal, or null.</summary>
        internal DescriptorStream RawInput {
            get { return (_consoleType == ConsoleStreamType.Input) ? GetUnbufferedInput() as DescriptorStream : null; }
        }

        /// <summary>
        /// STDIN.close: a reader parked in the terminal's descriptor stream is woken and raises IOError,
        /// as it does in MRI, and the next standard input stream gets a descriptor stream of its own.
        /// The descriptor itself stays open, as it did before.
        /// </summary>
        protected override void Dispose(bool disposing) {
            if (disposing && _consoleType == ConsoleStreamType.Input) {
                var raw = _rawInput;
                if (raw != null) {
                    _rawInput = null;
                    _rawInputChecked = false;
                    raw.Dispose();
                }
            }
            base.Dispose(disposing);
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
        /// (Hosting/WarmStart.cs) has just put different ones there - or, on Windows, different
        /// standard handles, which decide between the console writer and a pipe stream.
        /// </summary>
        internal static void ResetStandardDescriptors() {
            _rawInput = null;
            _rawInputChecked = false;
            _rawOutput = null;
            _rawError = null;
            _consoleOutput = null;
            _consoleError = null;
            _consoleOutputChecked = false;
            _consoleErrorChecked = false;
        }

        /// <summary>
        /// The pipe standard output or standard error writes to on Windows when it is redirected
        /// to one, or null. IO#syswrite on a non-blocking STDOUT writes to it directly, as a
        /// write(2) on descriptor 1 would.
        /// </summary>
        internal WindowsPipeStream WindowsPipe {
            get {
                if (_consoleType == ConsoleStreamType.Input) {
                    return null;
                }
                bool output = _consoleType == ConsoleStreamType.Output;
                return GetUnbufferedStream(output ? _io.OutputStream : _io.ErrorStream, output) as WindowsPipeStream;
            }
        }

        /// <summary>
        /// On Unix .NET's own standard-output stream emulates SIG_IGN for SIGPIPE by swallowing EPIPE
        /// outright, so `ruby -e 'loop { puts :ok }' | head -1` never notices that its reader is gone
        /// and spins forever instead of dying the way CRuby does.  A FileStream over the very same
        /// descriptor does report the error, so use one whenever the standard stream is still the one
        /// .NET opened (an embedder that redirected SharedIO keeps its own stream) and is redirected.
        /// </summary>
        private static Stream GetUnbufferedStream(Stream/*!*/ stream, bool output) {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && stream == Stream.Null) {
                // .NET checks the handle with a zero-byte WriteFile when it opens standard output,
                // and hands out Stream.Null when that fails - which it does on a pipe whose reader
                // has already gone. Everything written was then thrown away without an error, and
                // a child that IO.popen(...).close had abandoned before it wrote its first line
                // ran `loop { puts }` for ever. The handle itself still says EPIPE.
                return GetWindowsConsole(output);
            }

            var name = stream.GetType().FullName;
            if (name == null || !name.EndsWith("ConsoleStream", StringComparison.Ordinal)) {
                return null;
            }

            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return GetWindowsConsole(output);
            }
            if (Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX) {
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

        private static Stream _consoleOutput, _consoleError;
        private static bool _consoleOutputChecked, _consoleErrorChecked;

        /// <summary>
        /// On Windows .NET's standard-output stream hands the bytes to WriteFile, and a console
        /// reads them in its output code page - 850 or 437 on most machines, not UTF-8 - so
        /// everything outside ASCII came out as mojibake ("ä" as "Ã¤", irb's banner logo as
        /// "ÔóÇÔí"). MRI writes to a console with WriteConsoleW instead, as UTF-16, which shows
        /// the text whatever the code page; this does the same, decoding the bytes as UTF-8,
        /// Ruby's default external encoding on Windows. A stream that is redirected to a file
        /// or a pipe is not a console and keeps getting the bytes unchanged; a pipe through a
        /// WindowsPipeStream, which reports a reader that has gone.
        /// </summary>
        private static Stream GetWindowsConsole(bool output) {
            ref bool isChecked = ref (output ? ref _consoleOutputChecked : ref _consoleErrorChecked);
            ref Stream cache = ref (output ? ref _consoleOutput : ref _consoleError);
            if (!isChecked) {
                isChecked = true;
                try {
                    IntPtr handle = Win32Console.GetStdHandle(output ? Win32Console.STD_OUTPUT_HANDLE : Win32Console.STD_ERROR_HANDLE);
                    if (Win32Console.GetConsoleMode(handle, out _)) {
                        cache = new Win32ConsoleWriter(handle);
                    } else if (WindowsPipeStream.IsPipe(handle)) {
                        // .NET's stream ignores ERROR_NO_DATA, so a child writing to a pipe
                        // whose reader has gone - IO.popen(cmd).close - never learnt of it and
                        // `loop { puts }` ran for ever. This one reports EPIPE, as on Unix.
                        // The handle stays open: it is the process's, not this stream's.
                        cache = new WindowsPipeStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(handle, false), FileAccess.Write);
                    }
                } catch (Exception) {
                    cache = null;
                }
            }
            return cache;
        }

        private static class Win32Console {
            internal const int STD_OUTPUT_HANDLE = -11;
            internal const int STD_ERROR_HANDLE = -12;

            [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
            internal static extern IntPtr GetStdHandle(int nStdHandle);

            [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
            internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint mode);

            [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            internal static extern bool WriteConsoleW(IntPtr hConsoleOutput, char[] buffer, int charsToWrite, out int charsWritten, IntPtr reserved);
        }

        /// <summary>
        /// Writes UTF-8 bytes to a console as UTF-16. A character whose bytes arrive in two
        /// writes is held back until the rest of it comes, as the decoder keeps them.
        /// </summary>
        private sealed class Win32ConsoleWriter : Stream {
            private readonly IntPtr _handle;
            private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
            private char[] _chars = new char[1024];

            public Win32ConsoleWriter(IntPtr handle) {
                _handle = handle;
            }

            public override void Write(byte[]/*!*/ buffer, int offset, int count) {
                lock (_decoder) {
                    int needed = _decoder.GetCharCount(buffer, offset, count, false);
                    if (needed > _chars.Length) {
                        _chars = new char[Math.Max(needed, _chars.Length * 2)];
                    }
                    int length = _decoder.GetChars(buffer, offset, count, _chars, 0, false);
                    while (length > 0) {
                        if (!Win32Console.WriteConsoleW(_handle, _chars, length, out int written, IntPtr.Zero) || written <= 0) {
                            throw new IOException("WriteConsoleW failed", System.Runtime.InteropServices.Marshal.GetHRForLastWin32Error());
                        }
                        // The console took only part of it: what is left goes again from the start.
                        length -= written;
                        Array.Copy(_chars, written, _chars, 0, length);
                    }
                }
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override void Flush() { }
            public override long Length => throw new NotSupportedException();
            public override long Position {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}

/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.StandardLibrary.Termios {

    /// <summary>
    /// The terminal syscalls io/console is built on: tcgetattr/tcsetattr, tcflush and
    /// ioctl(TIOCGWINSZ/TIOCSWINSZ), on the descriptor the kernel knows an IO by.
    ///
    /// Only the primitives are here; everything Ruby-shaped - IO.console, IO#raw, #echo=,
    /// #getch, #winsize and the rest - is Ruby, in Src/StdLib/ironruby/io/console.rb.  That
    /// is the same split the Ripper library uses.  It follows the pattern RubyIO already
    /// sets for fcntl(2): P/Invoke into libc against RubyIO.KernelDescriptor, because
    /// IronRuby's #fileno is an index into its own table and naming it to a syscall asks
    /// about an unrelated descriptor.
    ///
    /// The constants are Linux's (asm-generic/termbits.h, asm-generic/ioctls.h).  On a
    /// platform without termios <see cref="Supported"/> is false and every call returns nil
    /// or false, which io/console.rb turns into the error MRI raises off a non-terminal -
    /// except winsize, which Windows answers from the console API, as MRI does there.
    /// </summary>
    [RubyModule("Termios")]
    public static class TermiosOps {

        /// <summary>
        /// struct termios on Linux: four 32-bit flag words, c_line, NCCS control characters,
        /// then the two speeds.  Laid out by hand rather than marshalled so that the exact
        /// 60-byte shape the kernel expects is visible here.
        /// </summary>
        private const int NCCS = 32;
        private const int TermiosSize = 60;
        private const int CcOffset = 17;
        private const int TCSANOW = 0;

        private const int TIOCGWINSZ = 0x5413;
        private const int TIOCSWINSZ = 0x5414;

        private static readonly bool _supported = System.IO.Path.DirectorySeparatorChar == '/';

        [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
        private static extern int sys_tcgetattr(int fd, byte[] termios);

        [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
        private static extern int sys_tcsetattr(int fd, int optionalActions, byte[] termios);

        [DllImport("libc", EntryPoint = "tcflush", SetLastError = true)]
        private static extern int sys_tcflush(int fd, int queueSelector);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int sys_ioctl(int fd, ulong request, byte[] argp);

        [DllImport("libc", EntryPoint = "isatty", SetLastError = true)]
        private static extern int sys_isatty(int fd);

        #region descriptor

        /// <summary>
        /// The descriptor the kernel knows this IO by, or -1.  io/console.rb needs it for
        /// nothing but its own sanity checks; every call below takes the IO itself.
        /// </summary>
        [RubyMethod("descriptor", RubyMethodAttributes.PublicSingleton)]
        public static int Descriptor(RubyModule/*!*/ self, [NotNull]RubyIO/*!*/ io) {
            return DescriptorOf(io);
        }

        private static int DescriptorOf(RubyIO/*!*/ io) {
            if (!_supported || io.Closed) {
                return -1;
            }
            return io.KernelDescriptor;
        }

        /// <summary>
        /// Whether this build can talk termios at all.
        /// </summary>
        [RubyMethod("supported?", RubyMethodAttributes.PublicSingleton)]
        public static bool Supported(RubyModule/*!*/ self) {
            return _supported;
        }

        /// <summary>
        /// isatty(2) on the real descriptor.  IO#tty? answers from IronRuby's own stream
        /// bookkeeping, which is right for its own streams but says nothing about a
        /// descriptor handed to it; io/console wants the kernel's answer.
        /// </summary>
        [RubyMethod("tty?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsTty(RubyModule/*!*/ self, [NotNull]RubyIO/*!*/ io) {
            int fd = DescriptorOf(io);
            return fd >= 0 && sys_isatty(fd) == 1;
        }

        #endregion

        #region tcgetattr / tcsetattr

        /// <summary>
        /// tcgetattr(3) as [iflag, oflag, cflag, lflag, cc], where cc is the NCCS control
        /// characters as a String of bytes.  nil when the descriptor is not a terminal.
        /// </summary>
        [RubyMethod("tcgetattr", RubyMethodAttributes.PublicSingleton)]
        public static object TcGetAttr(RubyModule/*!*/ self, [NotNull]RubyIO/*!*/ io) {
            int fd = DescriptorOf(io);
            if (fd < 0) {
                return null;
            }
            byte[] buffer = new byte[TermiosSize];
            if (sys_tcgetattr(fd, buffer) != 0) {
                return null;
            }
            var result = new RubyArray(5);
            for (int i = 0; i < 4; i++) {
                result.Add((int)BitConverter.ToUInt32(buffer, i * 4));
            }
            result.Add(MutableString.CreateBinary(Subarray(buffer, CcOffset, NCCS)));
            return result;
        }

        /// <summary>
        /// tcsetattr(3) with TCSANOW, from the same five-element description tcgetattr
        /// returns.  False when the descriptor is not a terminal or the call fails.
        /// </summary>
        [RubyMethod("tcsetattr", RubyMethodAttributes.PublicSingleton)]
        public static bool TcSetAttr(RubyModule/*!*/ self, [NotNull]RubyIO/*!*/ io, [NotNull]RubyArray/*!*/ attributes) {
            int fd = DescriptorOf(io);
            if (fd < 0 || attributes.Count < 5) {
                return false;
            }

            // Read the current state first: c_line and the two speeds are not part of the
            // description Ruby passes around, and handing the kernel zeroes for them would
            // reset the line discipline and the baud rate.
            byte[] buffer = new byte[TermiosSize];
            if (sys_tcgetattr(fd, buffer) != 0) {
                return false;
            }

            for (int i = 0; i < 4; i++) {
                if (!(attributes[i] is int)) {
                    return false;
                }
                BitConverter.GetBytes((uint)(int)attributes[i]).CopyTo(buffer, i * 4);
            }

            var cc = attributes[4] as MutableString;
            if (cc == null) {
                return false;
            }
            byte[] ccBytes = cc.ToByteArray();
            for (int i = 0; i < NCCS && i < ccBytes.Length; i++) {
                buffer[CcOffset + i] = ccBytes[i];
            }

            return sys_tcsetattr(fd, TCSANOW, buffer) == 0;
        }

        /// <summary>
        /// tcflush(3).  0 discards input, 1 output, 2 both - TCIFLUSH, TCOFLUSH, TCIOFLUSH.
        /// </summary>
        [RubyMethod("tcflush", RubyMethodAttributes.PublicSingleton)]
        public static bool TcFlush(RubyModule/*!*/ self, [NotNull]RubyIO/*!*/ io, [DefaultProtocol]int queueSelector) {
            int fd = DescriptorOf(io);
            return fd >= 0 && sys_tcflush(fd, queueSelector) == 0;
        }

        #endregion

        #region winsize

        /// <summary>
        /// ioctl(TIOCGWINSZ) as [rows, columns], or nil when this is not a terminal.  On Windows
        /// it is the console window's size, from GetConsoleScreenBufferInfo, as MRI's
        /// io/console answers it there - irb asks $stdout for it before it shows each result.
        /// </summary>
        [RubyMethod("winsize", RubyMethodAttributes.PublicSingleton)]
        public static object WinSize(RubyModule/*!*/ self, [NotNull]RubyIO/*!*/ io) {
            if (!_supported) {
                return WindowsWinSize(io);
            }
            int fd = DescriptorOf(io);
            if (fd < 0) {
                return null;
            }
            byte[] buffer = new byte[8];
            if (sys_ioctl(fd, TIOCGWINSZ, buffer) != 0) {
                return null;
            }
            var result = new RubyArray(2);
            result.Add((int)BitConverter.ToUInt16(buffer, 0));
            result.Add((int)BitConverter.ToUInt16(buffer, 2));
            return result;
        }

        /// <summary>
        /// ioctl(TIOCSWINSZ).  The pixel dimensions are carried over from the current size,
        /// as MRI does.
        /// </summary>
        [RubyMethod("set_winsize", RubyMethodAttributes.PublicSingleton)]
        public static bool SetWinSize(RubyModule/*!*/ self, [NotNull]RubyIO/*!*/ io,
            [DefaultProtocol]int rows, [DefaultProtocol]int columns) {

            int fd = DescriptorOf(io);
            if (fd < 0) {
                return false;
            }
            byte[] buffer = new byte[8];
            sys_ioctl(fd, TIOCGWINSZ, buffer);
            BitConverter.GetBytes((ushort)rows).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)columns).CopyTo(buffer, 2);
            return sys_ioctl(fd, TIOCSWINSZ, buffer) == 0;
        }

        private const int STD_INPUT_HANDLE = -10;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;

        /// <summary>
        /// CONSOLE_SCREEN_BUFFER_INFO: dwSize, dwCursorPosition (COORDs), wAttributes, then
        /// srWindow - Left, Top, Right, Bottom as shorts at 10, 12, 14 and 16 - and
        /// dwMaximumWindowSize.  22 bytes, laid out by hand like struct termios above.
        /// </summary>
        private const int ScreenBufferInfoSize = 22;

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, byte[] info);

        /// <summary>
        /// The visible window of the console a standard stream is attached to, as MRI computes
        /// it: srWindow.Bottom - Top + 1 rows and Right - Left + 1 columns.  nil for an IO
        /// that is no standard stream, and for one that is not a console - redirected, or
        /// the input side, which has no screen buffer.
        /// </summary>
        private static object WindowsWinSize(RubyIO/*!*/ io) {
            if (io.Closed) {
                return null;
            }
            int std;
            switch (io.ConsoleStreamType) {
                case ConsoleStreamType.Input: std = STD_INPUT_HANDLE; break;
                case ConsoleStreamType.Output: std = STD_OUTPUT_HANDLE; break;
                case ConsoleStreamType.ErrorOutput: std = STD_ERROR_HANDLE; break;
                default: return null;
            }
            byte[] info = new byte[ScreenBufferInfoSize];
            if (!GetConsoleScreenBufferInfo(GetStdHandle(std), info)) {
                return null;
            }
            short left = BitConverter.ToInt16(info, 10), top = BitConverter.ToInt16(info, 12);
            short right = BitConverter.ToInt16(info, 14), bottom = BitConverter.ToInt16(info, 16);
            var result = new RubyArray(2);
            result.Add(bottom - top + 1);
            result.Add(right - left + 1);
            return result;
        }

        #endregion

        #region constants

        // c_iflag
        [RubyConstant]
        public const int IGNBRK = 0x0001;
        [RubyConstant]
        public const int BRKINT = 0x0002;
        [RubyConstant]
        public const int PARMRK = 0x0008;
        [RubyConstant]
        public const int ISTRIP = 0x0020;
        [RubyConstant]
        public const int INLCR = 0x0040;
        [RubyConstant]
        public const int IGNCR = 0x0080;
        [RubyConstant]
        public const int ICRNL = 0x0100;
        [RubyConstant]
        public const int IXON = 0x0400;

        // c_oflag
        [RubyConstant]
        public const int OPOST = 0x0001;
        [RubyConstant]
        public const int ONLCR = 0x0004;

        // c_cflag
        [RubyConstant]
        public const int CSIZE = 0x0030;
        [RubyConstant]
        public const int CS8 = 0x0030;
        [RubyConstant]
        public const int PARENB = 0x0100;

        // c_lflag
        [RubyConstant]
        public const int ISIG = 0x0001;
        [RubyConstant]
        public const int ICANON = 0x0002;
        [RubyConstant]
        public const int ECHO = 0x0008;
        [RubyConstant]
        public const int ECHOE = 0x0010;
        [RubyConstant]
        public const int ECHOK = 0x0020;
        [RubyConstant]
        public const int ECHONL = 0x0040;
        [RubyConstant]
        public const int IEXTEN = 0x8000;

        // indices into c_cc
        [RubyConstant]
        public const int VINTR = 0;
        [RubyConstant]
        public const int VQUIT = 1;
        [RubyConstant]
        public const int VERASE = 2;
        [RubyConstant]
        public const int VKILL = 3;
        [RubyConstant]
        public const int VEOF = 4;
        [RubyConstant]
        public const int VTIME = 5;
        [RubyConstant]
        public const int VMIN = 6;
        [RubyConstant]
        public const int VSUSP = 10;

        // tcflush queue selectors
        [RubyConstant]
        public const int TCIFLUSH = 0;
        [RubyConstant]
        public const int TCOFLUSH = 1;
        [RubyConstant]
        public const int TCIOFLUSH = 2;

        #endregion

        private static byte[]/*!*/ Subarray(byte[]/*!*/ source, int start, int count) {
            byte[] result = new byte[count];
            Array.Copy(source, start, result, 0, count);
            return result;
        }
    }
}

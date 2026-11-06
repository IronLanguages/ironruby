/* ****************************************************************************
 *
 * sendmsg(2) and recvmsg(2).
 *
 * .NET exposes no scatter/gather or ancillary-data socket surface at all, which
 * is why BasicSocket#sendmsg and #recvmsg -- and everything built on them --
 * simply did not exist.  Everything they need is one libc call away, in the same
 * style as Src/Libraries/Builtins/Posix.cs, so this is the syscall layer and
 * BasicSocket.cs puts the Ruby methods on top of it.
 *
 * The struct layouts below are the Linux x86-64 and aarch64 ones, which are
 * identical to each other.
 *
 * ***************************************************************************/


using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IronRuby.StandardLibrary.Sockets {

    /// <summary>One cmsghdr: what Ruby calls a Socket::AncillaryData.</summary>
    internal sealed class ControlMessage {
        public readonly int Level;
        public readonly int Type;
        public readonly byte[]/*!*/ Data;

        public ControlMessage(int level, int type, byte[]/*!*/ data) {
            Level = level;
            Type = type;
            Data = data;
        }
    }

    /// <summary>What one recvmsg(2) produced.</summary>
    internal sealed class ReceivedMessage {
        public byte[] Data;
        public byte[] SenderAddress;    // null when the kernel reported no source address
        public int Flags;
        public List<ControlMessage> Controls;
    }

    internal static class PosixMessages {

        [DllImport("libc", EntryPoint = "socketpair", SetLastError = true)]
        internal static extern int socketpair(int domain, int type, int protocol, int[] sv);

        [DllImport("libc", EntryPoint = "getsockname", SetLastError = true)]
        private static extern int sys_getsockname(int fd, byte[] address, ref int length);

        [DllImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
        private static extern IntPtr sys_sendmsg(int fd, ref MsgHdr message, int flags);

        [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
        private static extern IntPtr sys_recvmsg(int fd, ref MsgHdr message, int flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct IOVec {
            public IntPtr Base;
            public IntPtr Length;
        }

        // struct msghdr. The two padding fields are explicit rather than implied because the
        // marshaller lays the struct out exactly as declared.
        [StructLayout(LayoutKind.Sequential)]
        private struct MsgHdr {
            public IntPtr Name;
            public int NameLength;
            public int NamePadding;
            public IntPtr IOV;
            public IntPtr IOVLength;
            public IntPtr Control;
            public IntPtr ControlLength;
            public int Flags;
            public int FlagsPadding;
        }

        internal const int SOL_SOCKET = 1;
        internal const int SCM_RIGHTS = 1;

        // struct cmsghdr is { size_t cmsg_len; int cmsg_level; int cmsg_type; }, so the payload
        // starts at 16 and CMSG_ALIGN rounds up to the next multiple of 8.
        private const int CmsgHeaderSize = 16;

        private static int CmsgAlign(int length) {
            return (length + 7) & ~7;
        }

        /// <summary>The largest sockaddr the kernel can hand back (sockaddr_storage).</summary>
        private const int MaxSocketAddressSize = 128;

        #region getsockname

        /// <summary>
        /// getsockname(2), or null if the kernel refused.  .NET only remembers an endpoint it
        /// set itself, so a socket the kernel auto-bound -- listen(2) on an unbound socket --
        /// has a null Socket.LocalEndPoint even though it very much has a name.
        /// </summary>
        internal static byte[] GetSocketName(int descriptor) {
            byte[] buffer = new byte[MaxSocketAddressSize];
            int length = buffer.Length;
            try {
                if (sys_getsockname(descriptor, buffer, ref length) != 0) {
                    return null;
                }
            } catch (Exception) {
                // No libc getsockname: nothing is lost, the caller falls back.
                return null;
            }
            if (length <= 0 || length > buffer.Length) {
                return null;
            }
            byte[] result = new byte[length];
            Array.Copy(buffer, result, length);
            return result;
        }

        #endregion

        #region getsockopt, setsockopt

        [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
        private static extern int sys_getsockopt(int fd, int level, int optname, byte[] value, ref int length);

        [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
        private static extern int sys_setsockopt(int fd, int level, int optname, byte[] value, int length);

        /// <summary>
        /// Whether socket options can go to libc. The platform numbers the Ruby layer translates
        /// to are Linux's, so this is Linux only.
        /// </summary>
        internal static readonly bool HasSocketOptions = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        /// getsockopt(2) with platform level/optname numbers. Answers the errno (0 on success)
        /// and the option bytes.
        /// </summary>
        internal static int GetSocketOption(int descriptor, int level, int optname, out byte[] value) {
            byte[] buffer = new byte[256];
            int length = buffer.Length;
            value = null;
            if (sys_getsockopt(descriptor, level, optname, buffer, ref length) != 0) {
                return Marshal.GetLastWin32Error();
            }
            value = new byte[Math.Max(0, Math.Min(length, buffer.Length))];
            Array.Copy(buffer, value, value.Length);
            return 0;
        }

        /// <summary>setsockopt(2) with platform level/optname numbers. Answers the errno, 0 on success.</summary>
        internal static int SetSocketOption(int descriptor, int level, int optname, byte[]/*!*/ value) {
            if (sys_setsockopt(descriptor, level, optname, value, value.Length) != 0) {
                return Marshal.GetLastWin32Error();
            }
            return 0;
        }

        #endregion

        #region sendmsg

        /// <summary>
        /// Returns the number of bytes sent, or -1 with errno set.
        /// </summary>
        internal static long Send(int descriptor, byte[]/*!*/ data, int flags, byte[] address,
            IList<ControlMessage> controls, out int errno) {
            return Send(descriptor, data, 0, data.Length, flags, address, controls, out errno);
        }

        internal static long Send(int descriptor, byte[]/*!*/ data, int offset, int count, int flags, byte[] address,
            IList<ControlMessage> controls, out int errno) {

            int controlSize = 0;
            if (controls != null) {
                foreach (ControlMessage control in controls) {
                    controlSize += CmsgAlign(CmsgHeaderSize + control.Data.Length);
                }
            }

            // sendmsg(2) with a zero-length iovec sends nothing at all on a stream socket, which
            // would silently drop ancillary data; one byte is the usual carrier. Ruby's own
            // send_io does exactly this.
            if (count == 0 && controlSize != 0) {
                data = new byte[1];
                offset = 0;
                count = 1;
            }

            IntPtr payloadBuffer = Marshal.AllocHGlobal(Math.Max(count, 1));
            IntPtr iov = Marshal.AllocHGlobal(Marshal.SizeOf<IOVec>());
            IntPtr addressBuffer = (address != null && address.Length != 0)
                ? Marshal.AllocHGlobal(address.Length) : IntPtr.Zero;
            IntPtr controlBuffer = (controlSize != 0) ? Marshal.AllocHGlobal(controlSize) : IntPtr.Zero;

            try {
                if (count != 0) {
                    Marshal.Copy(data, offset, payloadBuffer, count);
                }
                Marshal.StructureToPtr(new IOVec { Base = payloadBuffer, Length = (IntPtr)count }, iov, false);

                if (addressBuffer != IntPtr.Zero) {
                    Marshal.Copy(address, 0, addressBuffer, address.Length);
                }

                if (controlBuffer != IntPtr.Zero) {
                    int at = 0;
                    foreach (ControlMessage control in controls) {
                        int length = CmsgHeaderSize + control.Data.Length;
                        for (int i = at; i < at + CmsgAlign(length); i++) {
                            Marshal.WriteByte(controlBuffer, i, 0);
                        }
                        Marshal.WriteIntPtr(controlBuffer, at, (IntPtr)length);
                        Marshal.WriteInt32(controlBuffer, at + 8, control.Level);
                        Marshal.WriteInt32(controlBuffer, at + 12, control.Type);
                        Marshal.Copy(control.Data, 0, controlBuffer + at + CmsgHeaderSize, control.Data.Length);
                        at += CmsgAlign(length);
                    }
                }

                MsgHdr message = new MsgHdr {
                    Name = addressBuffer,
                    NameLength = (addressBuffer != IntPtr.Zero) ? address.Length : 0,
                    IOV = iov,
                    IOVLength = (IntPtr)1,
                    Control = controlBuffer,
                    ControlLength = (IntPtr)controlSize,
                };

                long sent = (long)sys_sendmsg(descriptor, ref message, flags);
                errno = (sent < 0) ? Marshal.GetLastWin32Error() : 0;
                return sent;
            } finally {
                if (controlBuffer != IntPtr.Zero) {
                    Marshal.FreeHGlobal(controlBuffer);
                }
                if (addressBuffer != IntPtr.Zero) {
                    Marshal.FreeHGlobal(addressBuffer);
                }
                Marshal.FreeHGlobal(iov);
                Marshal.FreeHGlobal(payloadBuffer);
            }
        }

        #endregion

        #region recvmsg

        /// <summary>
        /// Returns the message, or null with errno set.
        /// </summary>
        internal static ReceivedMessage Receive(int descriptor, int maxMessageLength, int flags,
            int maxControlLength, out int errno) {

            IntPtr payload = Marshal.AllocHGlobal(Math.Max(maxMessageLength, 1));
            IntPtr iov = Marshal.AllocHGlobal(Marshal.SizeOf<IOVec>());
            IntPtr addressBuffer = Marshal.AllocHGlobal(MaxSocketAddressSize);
            IntPtr controlBuffer = (maxControlLength != 0) ? Marshal.AllocHGlobal(maxControlLength) : IntPtr.Zero;

            try {
                Marshal.StructureToPtr(new IOVec { Base = payload, Length = (IntPtr)maxMessageLength }, iov, false);

                MsgHdr message = new MsgHdr {
                    Name = addressBuffer,
                    NameLength = MaxSocketAddressSize,
                    IOV = iov,
                    IOVLength = (IntPtr)1,
                    Control = controlBuffer,
                    ControlLength = (IntPtr)maxControlLength,
                };

                long received = (long)sys_recvmsg(descriptor, ref message, flags);
                if (received < 0) {
                    errno = Marshal.GetLastWin32Error();
                    return null;
                }
                errno = 0;

                var result = new ReceivedMessage();
                result.Data = new byte[received];
                if (received != 0) {
                    Marshal.Copy(payload, result.Data, 0, (int)received);
                }
                result.Flags = message.Flags;

                // A connected stream socket has no source address to report, and the kernel says
                // so by leaving msg_namelen at zero.
                if (message.NameLength > 0) {
                    result.SenderAddress = new byte[Math.Min(message.NameLength, MaxSocketAddressSize)];
                    Marshal.Copy(addressBuffer, result.SenderAddress, 0, result.SenderAddress.Length);
                }

                result.Controls = ReadControls(controlBuffer, (long)message.ControlLength);
                return result;
            } finally {
                if (controlBuffer != IntPtr.Zero) {
                    Marshal.FreeHGlobal(controlBuffer);
                }
                Marshal.FreeHGlobal(addressBuffer);
                Marshal.FreeHGlobal(iov);
                Marshal.FreeHGlobal(payload);
            }
        }

        private static List<ControlMessage>/*!*/ ReadControls(IntPtr buffer, long length) {
            var result = new List<ControlMessage>();
            if (buffer == IntPtr.Zero) {
                return result;
            }
            int offset = 0;
            while (length - offset >= CmsgHeaderSize) {
                long size = (long)Marshal.ReadIntPtr(buffer, offset);
                if (size < CmsgHeaderSize || size > length - offset) {
                    break;
                }
                int level = Marshal.ReadInt32(buffer, offset + 8);
                int type = Marshal.ReadInt32(buffer, offset + 12);
                byte[] data = new byte[size - CmsgHeaderSize];
                if (data.Length != 0) {
                    Marshal.Copy(buffer + offset + CmsgHeaderSize, data, 0, data.Length);
                }
                result.Add(new ControlMessage(level, type, data));
                offset += CmsgAlign((int)size);
            }
            return result;
        }

        #endregion

        #region descriptor passing

        /// <summary>Hands one descriptor to the peer as SCM_RIGHTS ancillary data.</summary>
        internal static long SendDescriptor(int socketDescriptor, int descriptor, out int errno) {
            var control = new ControlMessage(SOL_SOCKET, SCM_RIGHTS, BitConverter.GetBytes(descriptor));
            return Send(socketDescriptor, new byte[0], 0, null, new[] { control }, out errno);
        }

        /// <summary>
        /// Takes one descriptor out of the peer's SCM_RIGHTS ancillary data, or -1 if the peer
        /// sent data but no descriptor.
        /// </summary>
        internal static int ReceiveDescriptor(int socketDescriptor, out int errno) {
            ReceivedMessage message = Receive(socketDescriptor, 1, 0, CmsgAlign(CmsgHeaderSize + 4), out errno);
            if (message == null) {
                return -1;
            }
            foreach (ControlMessage control in message.Controls) {
                if (control.Level == SOL_SOCKET && control.Type == SCM_RIGHTS && control.Data.Length >= 4) {
                    return BitConverter.ToInt32(control.Data, 0);
                }
            }
            return -1;
        }

        #endregion
    }
}


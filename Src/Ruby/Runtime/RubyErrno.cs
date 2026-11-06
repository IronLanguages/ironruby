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
using System.Runtime.InteropServices;

namespace IronRuby.Runtime {
    /// <summary>
    /// The errno numbers and strerror(3) texts of MRI's Errno classes, for the platform IronRuby
    /// runs on. MRI takes both from the C library it was built against: Errno::ECONNREFUSED::Errno
    /// is 111 on Linux, 61 on macOS and 10061 (WSAECONNREFUSED) on Windows, and its message is
    /// "Connection refused" on the first two and the FormatMessage text on Windows. This is the one
    /// place both come from: the CLR-backed Errno classes read it, and so does
    /// Src/StdLib/ironruby/ruby4.rb, through Errno.__ir_errno_table__, for all the others.
    /// </summary>
    public static class RubyErrno {
        private enum Platform { Linux, Darwin, Windows }

        private static readonly Platform s_platform =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Platform.Windows :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ? Platform.Darwin :
            Platform.Linux;

        /// <summary>
        /// Every name MRI's Errno module has (defs/known_errors.def), sorted, with its number on Linux,
        /// macOS and Windows - where MRI's win32.h redefines the socket errors as their WSA codes.
        /// 0: the platform has no such error, and MRI binds the name to Errno::NOERROR. Where two
        /// names share a number, MRI makes the later one the earlier one's class (EWOULDBLOCK is
        /// EAGAIN on Linux and macOS, but a class of its own on Windows).
        /// </summary>
        private static readonly (string Name, int Linux, int Darwin, int Windows)[] s_table = {
            ("E2BIG", 7, 7, 7), ("EACCES", 13, 13, 13), ("EADDRINUSE", 98, 48, 10048),
            ("EADDRNOTAVAIL", 99, 49, 10049), ("EADV", 68, 0, 0), ("EAFNOSUPPORT", 97, 47, 10047),
            ("EAGAIN", 11, 35, 11), ("EALREADY", 114, 37, 10037), ("EAUTH", 0, 80, 0),
            ("EBADARCH", 0, 86, 0), ("EBADE", 52, 0, 0), ("EBADEXEC", 0, 85, 0),
            ("EBADF", 9, 9, 9), ("EBADFD", 77, 0, 0), ("EBADMACHO", 0, 88, 0),
            ("EBADMSG", 74, 94, 104), ("EBADR", 53, 0, 0), ("EBADRPC", 0, 72, 0),
            ("EBADRQC", 56, 0, 0), ("EBADSLT", 57, 0, 0), ("EBFONT", 59, 0, 0),
            ("EBUSY", 16, 16, 16), ("ECANCELED", 125, 89, 105), ("ECAPMODE", 0, 0, 0),
            ("ECHILD", 10, 10, 10), ("ECHRNG", 44, 0, 0), ("ECOMM", 70, 0, 0),
            ("ECONNABORTED", 103, 53, 10053), ("ECONNREFUSED", 111, 61, 10061), ("ECONNRESET", 104, 54, 10054),
            ("EDEADLK", 35, 11, 36), ("EDEADLOCK", 35, 0, 36), ("EDESTADDRREQ", 89, 39, 10039),
            ("EDEVERR", 0, 83, 0), ("EDOM", 33, 33, 33), ("EDOOFUS", 0, 0, 0),
            ("EDOTDOT", 73, 0, 0), ("EDQUOT", 122, 69, 10069), ("EEXIST", 17, 17, 17),
            ("EFAULT", 14, 14, 14), ("EFBIG", 27, 27, 27), ("EFTYPE", 0, 79, 0),
            ("EHOSTDOWN", 112, 64, 10064), ("EHOSTUNREACH", 113, 65, 10065), ("EHWPOISON", 133, 0, 0),
            ("EIDRM", 43, 90, 111), ("EILSEQ", 84, 92, 42), ("EINPROGRESS", 115, 36, 10036),
            ("EINTR", 4, 4, 4), ("EINVAL", 22, 22, 22), ("EIO", 5, 5, 5),
            ("EIPSEC", 0, 0, 0), ("EISCONN", 106, 56, 10056), ("EISDIR", 21, 21, 21),
            ("EISNAM", 120, 0, 0), ("EKEYEXPIRED", 127, 0, 0), ("EKEYREJECTED", 129, 0, 0),
            ("EKEYREVOKED", 128, 0, 0), ("EL2HLT", 51, 0, 0), ("EL2NSYNC", 45, 0, 0),
            ("EL3HLT", 46, 0, 0), ("EL3RST", 47, 0, 0), ("ELAST", 0, 106, 0),
            ("ELIBACC", 79, 0, 0), ("ELIBBAD", 80, 0, 0), ("ELIBEXEC", 83, 0, 0),
            ("ELIBMAX", 82, 0, 0), ("ELIBSCN", 81, 0, 0), ("ELNRNG", 48, 0, 0),
            ("ELOOP", 40, 62, 10062), ("EMEDIUMTYPE", 124, 0, 0), ("EMFILE", 24, 24, 24),
            ("EMLINK", 31, 31, 31), ("EMSGSIZE", 90, 40, 10040), ("EMULTIHOP", 72, 95, 0),
            ("ENAMETOOLONG", 36, 63, 38), ("ENAVAIL", 119, 0, 0), ("ENEEDAUTH", 0, 81, 0),
            ("ENETDOWN", 100, 50, 10050), ("ENETRESET", 102, 52, 10052), ("ENETUNREACH", 101, 51, 10051),
            ("ENFILE", 23, 23, 23), ("ENOANO", 55, 0, 0), ("ENOATTR", 0, 93, 0),
            ("ENOBUFS", 105, 55, 10055), ("ENOCSI", 50, 0, 0), ("ENODATA", 61, 96, 120),
            ("ENODEV", 19, 19, 19), ("ENOENT", 2, 2, 2), ("ENOEXEC", 8, 8, 8),
            ("ENOKEY", 126, 0, 0), ("ENOLCK", 37, 77, 39), ("ENOLINK", 67, 97, 121),
            ("ENOMEDIUM", 123, 0, 0), ("ENOMEM", 12, 12, 12), ("ENOMSG", 42, 91, 122),
            ("ENONET", 64, 0, 0), ("ENOPKG", 65, 0, 0), ("ENOPOLICY", 0, 103, 0),
            ("ENOPROTOOPT", 92, 42, 10042), ("ENOSPC", 28, 28, 28), ("ENOSR", 63, 98, 124),
            ("ENOSTR", 60, 99, 125), ("ENOSYS", 38, 78, 40), ("ENOTBLK", 15, 15, 0),
            ("ENOTCAPABLE", 0, 0, 0), ("ENOTCONN", 107, 57, 10057), ("ENOTDIR", 20, 20, 20),
            ("ENOTEMPTY", 39, 66, 41), ("ENOTNAM", 118, 0, 0), ("ENOTRECOVERABLE", 131, 104, 127),
            ("ENOTSOCK", 88, 38, 10038), ("ENOTSUP", 95, 45, 129), ("ENOTTY", 25, 25, 25),
            ("ENOTUNIQ", 76, 0, 0), ("ENXIO", 6, 6, 6), ("EOPNOTSUPP", 95, 102, 10045),
            ("EOVERFLOW", 75, 84, 132), ("EOWNERDEAD", 130, 105, 133), ("EPERM", 1, 1, 1),
            ("EPFNOSUPPORT", 96, 46, 10046), ("EPIPE", 32, 32, 32), ("EPROCLIM", 0, 67, 10067),
            ("EPROCUNAVAIL", 0, 76, 0), ("EPROGMISMATCH", 0, 75, 0), ("EPROGUNAVAIL", 0, 74, 0),
            ("EPROTO", 71, 100, 134), ("EPROTONOSUPPORT", 93, 43, 10043), ("EPROTOTYPE", 91, 41, 10041),
            ("EPWROFF", 0, 82, 0), ("EQFULL", 0, 106, 0), ("ERANGE", 34, 34, 34),
            ("EREMCHG", 78, 0, 0), ("EREMOTE", 66, 71, 10071), ("EREMOTEIO", 121, 0, 0),
            ("ERESTART", 85, 0, 0), ("ERFKILL", 132, 0, 0), ("EROFS", 30, 30, 30),
            ("ERPCMISMATCH", 0, 73, 0), ("ESHLIBVERS", 0, 87, 0), ("ESHUTDOWN", 108, 58, 10058),
            ("ESOCKTNOSUPPORT", 94, 44, 10044), ("ESPIPE", 29, 29, 29), ("ESRCH", 3, 3, 3),
            ("ESRMNT", 69, 0, 0), ("ESTALE", 116, 70, 10070), ("ESTRPIPE", 86, 0, 0),
            ("ETIME", 62, 101, 137), ("ETIMEDOUT", 110, 60, 10060), ("ETOOMANYREFS", 109, 59, 10059),
            ("ETXTBSY", 26, 26, 139), ("EUCLEAN", 117, 0, 0), ("EUNATCH", 49, 0, 0),
            ("EUSERS", 87, 68, 10068), ("EWOULDBLOCK", 11, 35, 10035), ("EXDEV", 18, 18, 18),
            ("EXFULL", 54, 0, 0),
        };

        /// <summary>
        /// The Windows C runtime's strerror for 0..42 (its sys_nerr). MRI's rb_w32_strerror asks the
        /// CRT for these and FormatMessage for every larger number, the WSA codes among them.
        /// </summary>
        private static readonly string[] s_crtMessages = {
            "No error", "Operation not permitted", "No such file or directory", "No such process",
            "Interrupted function call", "Input/output error", "No such device or address", "Arg list too long",
            "Exec format error", "Bad file descriptor", "No child processes", "Resource temporarily unavailable",
            "Not enough space", "Permission denied", "Bad address", "Unknown error", "Resource device",
            "File exists", "Improper link", "No such device", "Not a directory", "Is a directory",
            "Invalid argument", "Too many open files in system", "Too many open files",
            "Inappropriate I/O control operation", "Unknown error", "File too large", "No space left on device",
            "Invalid seek", "Read-only file system", "Too many links", "Broken pipe", "Domain error",
            "Result too large", "Unknown error", "Resource deadlock avoided", "Unknown error", "Filename too long",
            "No locks available", "Function not implemented", "Directory not empty", "Illegal byte sequence",
        };

        /// <summary>Every name, sorted - the order MRI defines the classes in.</summary>
        public static IEnumerable<string>/*!*/ Names {
            get {
                foreach (var entry in s_table) {
                    yield return entry.Name;
                }
            }
        }

        /// <summary>The errno <paramref name="name"/> has on this platform, or 0 if it has none.</summary>
        public static int GetNumber(string/*!*/ name) {
            int lo = 0, hi = s_table.Length - 1;
            while (lo <= hi) {
                int mid = (lo + hi) / 2;
                int cmp = String.CompareOrdinal(s_table[mid].Name, name);
                if (cmp == 0) {
                    var entry = s_table[mid];
                    return s_platform switch {
                        Platform.Darwin => entry.Darwin,
                        Platform.Windows => entry.Windows,
                        _ => entry.Linux,
                    };
                }
                if (cmp < 0) {
                    lo = mid + 1;
                } else {
                    hi = mid - 1;
                }
            }
            return 0;
        }

        /// <summary>strerror(errno), as MRI reports it on this platform.</summary>
        public static string/*!*/ GetMessage(int errno) {
            if (s_platform == Platform.Windows && errno >= 0 && errno < s_crtMessages.Length) {
                return s_crtMessages[errno];
            }
            return Marshal.GetPInvokeErrorMessage(errno);
        }

        /// <summary>The message of the error <paramref name="name"/> names on this platform.</summary>
        public static string/*!*/ GetMessage(string/*!*/ name) {
            int errno = GetNumber(name);
            return errno != 0 ? GetMessage(errno) : "Unknown error";
        }
    }
}

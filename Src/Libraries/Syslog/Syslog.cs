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
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Syslog {

    /// <summary>
    /// The four syscalls under Syslog. Everything else about the module - its constants, the
    /// state it remembers and the sprintf formatting - is Ruby, in
    /// Src/StdLib/ironruby/syslog.rb; only what needs libc is here.
    /// </summary>
    [RubyModule("Syslog")]
    public static class SyslogOps {

        [DllImport("libc", EntryPoint = "openlog", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sys_openlog(IntPtr ident, int option, int facility);

        // syslog(3) is variadic, and the format is "%s" every time so that a message containing
        // a percent sign is a message rather than a conversion.
        [DllImport("libc", EntryPoint = "syslog", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private static extern void sys_syslog(int priority, string/*!*/ format, IntPtr message);

        [DllImport("libc", EntryPoint = "closelog", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sys_closelog();

        [DllImport("libc", EntryPoint = "setlogmask", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sys_setlogmask(int mask);

        // openlog(3) keeps the pointer it is handed rather than copying the string behind it, so
        // the identity has to stay allocated for as long as the log is open - a managed string
        // marshalled for the call would be freed the moment it returned, and syslogd would read
        // whatever was left there.
        private static IntPtr _ident;

        [RubyMethod("__openlog__", RubyMethodAttributes.PrivateSingleton)]
        public static void OpenLog(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ ident,
            [DefaultProtocol]int option, [DefaultProtocol]int facility) {

            FreeIdent();
            _ident = Marshal.StringToHGlobalAnsi(ident.ToString());
            sys_openlog(_ident, option, facility);
        }

        [RubyMethod("__syslog__", RubyMethodAttributes.PrivateSingleton)]
        public static void Log(RubyModule/*!*/ self, [DefaultProtocol]int priority,
            [DefaultProtocol, NotNull]MutableString/*!*/ message) {

            IntPtr text = Marshal.StringToHGlobalAnsi(message.ToString());
            try {
                sys_syslog(priority, "%s", text);
            } finally {
                Marshal.FreeHGlobal(text);
            }
        }

        [RubyMethod("__closelog__", RubyMethodAttributes.PrivateSingleton)]
        public static void CloseLog(RubyModule/*!*/ self) {
            sys_closelog();
            FreeIdent();
        }

        [RubyMethod("__setlogmask__", RubyMethodAttributes.PrivateSingleton)]
        public static int SetLogMask(RubyModule/*!*/ self, [DefaultProtocol]int mask) {
            return sys_setlogmask(mask);
        }

        private static void FreeIdent() {
            if (_ident != IntPtr.Zero) {
                Marshal.FreeHGlobal(_ident);
                _ident = IntPtr.Zero;
            }
        }
    }
}

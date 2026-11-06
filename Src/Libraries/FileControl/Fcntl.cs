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

using IronRuby.Runtime;
using IronRuby.Builtins;

namespace IronRuby.StandardLibrary.FileControl {

    [RubyModule("Fcntl")]
    public class Fcntl {

        // The platform's own commands: IO#fcntl hands them to fcntl(2) as they are. The first five
        // are the same on every Unix; the record locks are not.
        [RubyConstant]
        public const int F_DUPFD = 0;

        [RubyConstant]
        public const int F_GETFD = 1;

        [RubyConstant]
        public const int F_SETFD = 2;

        [RubyConstant]
        public const int F_GETFL = 3;

        [RubyConstant]
        public const int F_SETFL = 4;

        [RubyConstant]
        public static readonly int F_GETLK = IOModeNative.Pick(5, 7, 11, 5);

        [RubyConstant]
        public static readonly int F_SETLK = IOModeNative.Pick(6, 8, 12, 6);

        [RubyConstant]
        public static readonly int F_SETLKW = IOModeNative.Pick(7, 9, 13, 7);

        [RubyConstant]
        public const int FD_CLOEXEC = 1;

        [RubyConstant]
        public static readonly int F_RDLCK = IOModeNative.Pick(0, 1, 1, 0);

        [RubyConstant]
        public static readonly int F_WRLCK = IOModeNative.Pick(1, 3, 3, 1);

        [RubyConstant]
        public const int F_UNLCK = 2;

        // Linux only.
        [RubyConstant]
        public static readonly object F_GETPIPE_SZ = LibraryInitializer.PlatformConstant(IOModeNative.Pick(1032, 0, 0, 0));

        [RubyConstant]
        public static readonly object F_SETPIPE_SZ = LibraryInitializer.PlatformConstant(IOModeNative.Pick(1031, 0, 0, 0));

        // The open(2) status flags, the platform's own values - fcntl(2) reads them back and
        // F_SETFL sets them, so they are the same numbers File::Constants uses.
        [RubyConstant]
        public readonly static int O_CREAT = IOModeNative.CreateIfNotExists;

        [RubyConstant]
        public readonly static int O_EXCL = IOModeNative.ErrorIfExists;

        [RubyConstant]
        public readonly static int O_TRUNC = IOModeNative.Truncate;

        [RubyConstant]
        public readonly static int O_APPEND = IOModeNative.Append;

        [RubyConstant]
        public readonly static int O_NONBLOCK = IOModeNative.NonBlocking;

        [RubyConstant]
        public readonly static int O_NOCTTY = IOModeNative.NoControllingTerminal;

        // MRI's Fcntl spells O_NONBLOCK twice, under its older name as well.
        [RubyConstant]
        public readonly static int O_NDELAY = IOModeNative.NonBlocking;

        [RubyConstant]
        public const int O_RDONLY = 0x0000;

        [RubyConstant]
        public const int O_RDWR = 0x0002;

        [RubyConstant]
        public const int O_WRONLY = 0x0001;

        [RubyConstant]
        public const int O_ACCMODE = O_RDONLY | O_WRONLY | O_RDWR;
    }
}

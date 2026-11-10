/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace IronRuby.Runtime {
    /// <summary>
    /// Whether the stack has room for another Ruby frame (RubyOps.RequireSufficientStack, #128). The margin it keeps is
    /// what a SystemStackError needs on its way out: the CLR runs exception filters in a first pass on top of the
    /// deepest frame, and the interpreter's filters match `rescue' clauses by calling Ruby, which can bind and compile
    /// call sites there. RuntimeHelpers.TryEnsureSufficientExecutionStack keeps 64 KB, too little for that, and the
    /// process died of a stack overflow anyway. So the thread's stack bounds are asked of the OS once per thread, and
    /// a quarter of the stack, at most 256 KB, is kept free.
    /// </summary>
    internal static unsafe class StackLimit {
        private const long MaxMargin = 256 * 1024;

        // the lowest address a Ruby frame may start at on this thread; 0: not asked yet; 1: the OS cannot say
        [ThreadStatic]
        private static ulong _limit;

        // The main thread's usable stack, [_mainLow, _mainLow + _mainSize), once it has been asked for. A thread-static
        // read costs more than the rest of the check in JIT-compiled code (fib(30) ran 2.3x slower), so an address in
        // this range, which no other thread's stack can share while the main thread lives, needs no lookup.
        internal static ulong _mainLow, _mainSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSufficient() {
            byte probe;
            ulong sp = (ulong)&probe;
            return sp - _mainLow < _mainSize || IsSufficientOffMain(sp);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsSufficientOffMain(ulong sp) {
            ulong limit = _limit;
            if (limit > 1) {
                return sp > limit;
            }
            if (limit == 0) {
                ulong high;
                _limit = QueryLimit(out high);
                // the main thread keeps its stack as long as the process runs; a thread that ends may hand its
                // addresses to another one with a different limit
                if (_limit > 1 && Environment.CurrentManagedThreadId == 1 && _mainSize == 0) {
                    _mainLow = _limit;
                    Volatile.Write(ref _mainSize, high - _limit);
                }
            }
            return (_limit > 1) ? sp > _limit : RuntimeHelpers.TryEnsureSufficientExecutionStack();
        }

        /// <summary>
        /// Whether the stack has <paramref name="bytes"/> to spare above the limit IsSufficient keeps (false when the OS cannot
        /// say). For code that is about to run on top of the current stack where it would otherwise have run lower down.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool HasRoom(ulong bytes) {
            byte probe;
            ulong sp = (ulong)&probe;
            if (!IsSufficient()) {
                return false;
            }
            ulong limit = (sp - _mainLow < _mainSize) ? _mainLow : _limit;
            return limit > 1 && sp - limit >= bytes;
        }

        private static ulong QueryLimit(out ulong high) {
            ulong low;
            high = 0;
            try {
                if (!TryGetStackBounds(out low, out high) || high <= low) {
                    return 1;
                }
            } catch (Exception) {
                // no such function here (an older libc, an unusual platform): the runtime's own check stands in
                return 1;
            }
            long margin = Math.Min(MaxMargin, (long)(high - low) / 4);
            return low + (ulong)margin;
        }

        private static bool TryGetStackBounds(out ulong low, out ulong high) {
            low = high = 0;
            if (OperatingSystem.IsWindows()) {
                UIntPtr l, h;
                GetCurrentThreadStackLimits(out l, out h);
                low = (ulong)l;
                high = (ulong)h;
                return true;
            }
            if (OperatingSystem.IsMacOS()) {
                IntPtr self = MacPthreadSelf();
                high = (ulong)pthread_get_stackaddr_np(self);
                low = high - (ulong)pthread_get_stacksize_np(self);
                return true;
            }
            if (OperatingSystem.IsLinux()) {
                // pthread_attr_t is 56 bytes on 64-bit glibc and 36 on musl; room for either
                byte* attr = stackalloc byte[256];
                if (pthread_getattr_np(pthread_self(), attr) != 0) {
                    return false;
                }
                try {
                    IntPtr address;
                    UIntPtr size;
                    if (pthread_attr_getstack(attr, out address, out size) != 0) {
                        return false;
                    }
                    low = (ulong)address;
                    high = low + (ulong)size;
                    return true;
                } finally {
                    pthread_attr_destroy(attr);
                }
            }
            return false;
        }

        [DllImport("kernel32.dll")]
        private static extern void GetCurrentThreadStackLimits(out UIntPtr lowLimit, out UIntPtr highLimit);

        [DllImport("libc")]
        private static extern IntPtr pthread_self();

        [DllImport("libc")]
        private static extern int pthread_getattr_np(IntPtr thread, byte* attr);

        [DllImport("libc")]
        private static extern int pthread_attr_getstack(byte* attr, out IntPtr address, out UIntPtr size);

        [DllImport("libc")]
        private static extern int pthread_attr_destroy(byte* attr);

        [DllImport("libc", EntryPoint = "pthread_self")]
        private static extern IntPtr MacPthreadSelf();

        [DllImport("libc")]
        private static extern IntPtr pthread_get_stackaddr_np(IntPtr thread);

        [DllImport("libc")]
        private static extern UIntPtr pthread_get_stacksize_np(IntPtr thread);
    }
}

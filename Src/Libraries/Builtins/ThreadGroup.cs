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
#if FEATURE_THREAD
using System.Threading;
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    [RubyClass("ThreadGroup", Inherits = typeof(object), BuildConfig = "FEATURE_THREAD")]
    public class ThreadGroup {
        private bool _enclosed;

        [RubyMethod("add")]
        public static ThreadGroup/*!*/ Add([NotNull]ThreadGroup/*!*/ self, [NotNull]Thread/*!*/ thread) {
            ThreadOps.RubyThreadInfo info = ThreadOps.RubyThreadInfo.FromThread(thread);

            // An enclosed group will not let a thread leave it, and will not take one either.
            ThreadGroup current = info.Group;
            if (current != null && current != self && current._enclosed) {
                throw new ThreadError("can't move from the enclosed thread group");
            }
            if (self._enclosed && current != self) {
                throw new ThreadError("can't move to the enclosed thread group");
            }

            info.Group = self;
            return self;
        }

        /// <summary>
        /// Locks the membership of the group: no thread may be added to or removed from it after
        /// this, and it cannot be undone.
        /// </summary>
        [RubyMethod("enclose")]
        public static ThreadGroup/*!*/ Enclose([NotNull]ThreadGroup/*!*/ self) {
            self._enclosed = true;
            return self;
        }

        [RubyMethod("enclosed?")]
        public static bool IsEnclosed([NotNull]ThreadGroup/*!*/ self) {
            return self._enclosed;
        }

        // #<ThreadGroup:0x...>, not the CLR type name a CLR-backed object would otherwise show
        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, [NotNull]ThreadGroup/*!*/ self) {
            return RubyUtils.ObjectToMutableString(context, self);
        }

        [RubyMethod("list")]
        public static RubyArray/*!*/ List([NotNull]ThreadGroup/*!*/ self) {
            ThreadOps.RubyThreadInfo[] threads = ThreadOps.RubyThreadInfo.Threads;
            RubyArray result = new RubyArray(threads.Length);
            foreach (ThreadOps.RubyThreadInfo threadInfo in threads) {
                Thread thread = threadInfo.Thread;
                if (thread != null && threadInfo.Group == self) {
                    result.Add(thread);
                }
            }

            return result;
        }

        [RubyConstant]
        public readonly static ThreadGroup Default = new ThreadGroup();
    }
}
#endif
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
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime.Calls {
    // The LibraryDispatcher of each site delegate shape: Func<CallSite, (scope)?, object self, (block)?, object x N, object>,
    // N = 0..MaxLibraryArity. They differ only in their parameter lists.
    public abstract partial class LibraryDispatcher {
        internal const int MaxLibraryArity = 5;

        private static LibraryDispatcher/*!*/ CreateDispatcher(int count, bool hasScope, bool hasBlock) {
            switch (count) {
                case 0: return hasScope ? (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithScopeAndBlock0() : new LibraryDispatcherWithScope0()) :
                    (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithBlock0() : new LibraryDispatcher0());
                case 1: return hasScope ? (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithScopeAndBlock1() : new LibraryDispatcherWithScope1()) :
                    (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithBlock1() : new LibraryDispatcher1());
                case 2: return hasScope ? (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithScopeAndBlock2() : new LibraryDispatcherWithScope2()) :
                    (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithBlock2() : new LibraryDispatcher2());
                case 3: return hasScope ? (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithScopeAndBlock3() : new LibraryDispatcherWithScope3()) :
                    (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithBlock3() : new LibraryDispatcher3());
                case 4: return hasScope ? (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithScopeAndBlock4() : new LibraryDispatcherWithScope4()) :
                    (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithBlock4() : new LibraryDispatcher4());
                case 5: return hasScope ? (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithScopeAndBlock5() : new LibraryDispatcherWithScope5()) :
                    (hasBlock ? (LibraryDispatcher)new LibraryDispatcherWithBlock5() : new LibraryDispatcher5());
                default: throw Assert.Unreachable;
            }
        }
    }

    public sealed class LibraryDispatcher0 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, object, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, object self) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, null, ArrayUtils.EmptyObjects, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, object>>)callSite).Update(callSite, self);
        }
    }

    public sealed class LibraryDispatcherWithBlock0 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, object, Proc, object>(Invoke<Proc>);
        }

        public object Invoke<TProc>(CallSite/*!*/ callSite, object self, TProc proc) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, (object)proc, ArrayUtils.EmptyObjects, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, TProc, object>>)callSite).Update(callSite, self, proc);
        }
    }

    public sealed class LibraryDispatcherWithScope0 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, RubyScope, object, object>(Invoke<RubyScope>);
        }

        public object Invoke<TScope>(CallSite/*!*/ callSite, TScope scope, object self) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, null, ArrayUtils.EmptyObjects, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, object>>)callSite).Update(callSite, scope, self);
        }
    }

    public sealed class LibraryDispatcherWithScopeAndBlock0 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object>(Invoke<object, object>) :
                (object)new Func<CallSite, RubyScope, object, Proc, object>(Invoke<RubyScope, Proc>);
        }

        public object Invoke<TScope, TProc>(CallSite/*!*/ callSite, TScope scope, object self, TProc proc) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, (object)proc, ArrayUtils.EmptyObjects, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, TProc, object>>)callSite).Update(callSite, scope, self, proc);
        }
    }

    public sealed class LibraryDispatcher1 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, object, object, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, object self, object arg0) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, null, new object[] { arg0 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, object, object>>)callSite).Update(callSite, self, arg0);
        }
    }

    public sealed class LibraryDispatcherWithBlock1 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, object, Proc, object, object>(Invoke<Proc>);
        }

        public object Invoke<TProc>(CallSite/*!*/ callSite, object self, TProc proc, object arg0) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, (object)proc, new object[] { arg0 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, TProc, object, object>>)callSite).Update(callSite, self, proc, arg0);
        }
    }

    public sealed class LibraryDispatcherWithScope1 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, RubyScope, object, object, object>(Invoke<RubyScope>);
        }

        public object Invoke<TScope>(CallSite/*!*/ callSite, TScope scope, object self, object arg0) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, null, new object[] { arg0 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, object, object>>)callSite).Update(callSite, scope, self, arg0);
        }
    }

    public sealed class LibraryDispatcherWithScopeAndBlock1 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object>(Invoke<object, object>) :
                (object)new Func<CallSite, RubyScope, object, Proc, object, object>(Invoke<RubyScope, Proc>);
        }

        public object Invoke<TScope, TProc>(CallSite/*!*/ callSite, TScope scope, object self, TProc proc, object arg0) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, (object)proc, new object[] { arg0 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, TProc, object, object>>)callSite).Update(callSite, scope, self, proc, arg0);
        }
    }

    public sealed class LibraryDispatcher2 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, object, object, object, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, object self, object arg0, object arg1) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, null, new object[] { arg0, arg1 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, object, object, object>>)callSite).Update(callSite, self, arg0, arg1);
        }
    }

    public sealed class LibraryDispatcherWithBlock2 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, object, Proc, object, object, object>(Invoke<Proc>);
        }

        public object Invoke<TProc>(CallSite/*!*/ callSite, object self, TProc proc, object arg0, object arg1) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, (object)proc, new object[] { arg0, arg1 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, TProc, object, object, object>>)callSite).Update(callSite, self, proc, arg0, arg1);
        }
    }

    public sealed class LibraryDispatcherWithScope2 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, RubyScope, object, object, object, object>(Invoke<RubyScope>);
        }

        public object Invoke<TScope>(CallSite/*!*/ callSite, TScope scope, object self, object arg0, object arg1) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, null, new object[] { arg0, arg1 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, object, object, object>>)callSite).Update(callSite, scope, self, arg0, arg1);
        }
    }

    public sealed class LibraryDispatcherWithScopeAndBlock2 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object>(Invoke<object, object>) :
                (object)new Func<CallSite, RubyScope, object, Proc, object, object, object>(Invoke<RubyScope, Proc>);
        }

        public object Invoke<TScope, TProc>(CallSite/*!*/ callSite, TScope scope, object self, TProc proc, object arg0, object arg1) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, (object)proc, new object[] { arg0, arg1 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, TProc, object, object, object>>)callSite).Update(callSite, scope, self, proc, arg0, arg1);
        }
    }

    public sealed class LibraryDispatcher3 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, object, object, object, object, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, object self, object arg0, object arg1, object arg2) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, null, new object[] { arg0, arg1, arg2 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, object, object, object, object>>)callSite).Update(callSite, self, arg0, arg1, arg2);
        }
    }

    public sealed class LibraryDispatcherWithBlock3 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, object, Proc, object, object, object, object>(Invoke<Proc>);
        }

        public object Invoke<TProc>(CallSite/*!*/ callSite, object self, TProc proc, object arg0, object arg1, object arg2) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, (object)proc, new object[] { arg0, arg1, arg2 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, TProc, object, object, object, object>>)callSite).Update(callSite, self, proc, arg0, arg1, arg2);
        }
    }

    public sealed class LibraryDispatcherWithScope3 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, RubyScope, object, object, object, object, object>(Invoke<RubyScope>);
        }

        public object Invoke<TScope>(CallSite/*!*/ callSite, TScope scope, object self, object arg0, object arg1, object arg2) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, null, new object[] { arg0, arg1, arg2 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, object, object, object, object>>)callSite).Update(callSite, scope, self, arg0, arg1, arg2);
        }
    }

    public sealed class LibraryDispatcherWithScopeAndBlock3 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object, object>(Invoke<object, object>) :
                (object)new Func<CallSite, RubyScope, object, Proc, object, object, object, object>(Invoke<RubyScope, Proc>);
        }

        public object Invoke<TScope, TProc>(CallSite/*!*/ callSite, TScope scope, object self, TProc proc, object arg0, object arg1, object arg2) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, (object)proc, new object[] { arg0, arg1, arg2 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, TProc, object, object, object, object>>)callSite).Update(callSite, scope, self, proc, arg0, arg1, arg2);
        }
    }

    public sealed class LibraryDispatcher4 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, object, object, object, object, object, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, object self, object arg0, object arg1, object arg2, object arg3) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, null, new object[] { arg0, arg1, arg2, arg3 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, object, object, object, object, object>>)callSite).Update(callSite, self, arg0, arg1, arg2, arg3);
        }
    }

    public sealed class LibraryDispatcherWithBlock4 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, object, Proc, object, object, object, object, object>(Invoke<Proc>);
        }

        public object Invoke<TProc>(CallSite/*!*/ callSite, object self, TProc proc, object arg0, object arg1, object arg2, object arg3) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, (object)proc, new object[] { arg0, arg1, arg2, arg3 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, TProc, object, object, object, object, object>>)callSite).Update(callSite, self, proc, arg0, arg1, arg2, arg3);
        }
    }

    public sealed class LibraryDispatcherWithScope4 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, RubyScope, object, object, object, object, object, object>(Invoke<RubyScope>);
        }

        public object Invoke<TScope>(CallSite/*!*/ callSite, TScope scope, object self, object arg0, object arg1, object arg2, object arg3) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, null, new object[] { arg0, arg1, arg2, arg3 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, object, object, object, object, object>>)callSite).Update(callSite, scope, self, arg0, arg1, arg2, arg3);
        }
    }

    public sealed class LibraryDispatcherWithScopeAndBlock4 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object, object, object>(Invoke<object, object>) :
                (object)new Func<CallSite, RubyScope, object, Proc, object, object, object, object, object>(Invoke<RubyScope, Proc>);
        }

        public object Invoke<TScope, TProc>(CallSite/*!*/ callSite, TScope scope, object self, TProc proc, object arg0, object arg1, object arg2, object arg3) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, (object)proc, new object[] { arg0, arg1, arg2, arg3 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, TProc, object, object, object, object, object>>)callSite).Update(callSite, scope, self, proc, arg0, arg1, arg2, arg3);
        }
    }

    public sealed class LibraryDispatcher5 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return new Func<CallSite, object, object, object, object, object, object, object>(Invoke);
        }

        public object Invoke(CallSite/*!*/ callSite, object self, object arg0, object arg1, object arg2, object arg3, object arg4) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, null, new object[] { arg0, arg1, arg2, arg3, arg4 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, object, object, object, object, object, object>>)callSite).Update(callSite, self, arg0, arg1, arg2, arg3, arg4);
        }
    }

    public sealed class LibraryDispatcherWithBlock5 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, object, Proc, object, object, object, object, object, object>(Invoke<Proc>);
        }

        public object Invoke<TProc>(CallSite/*!*/ callSite, object self, TProc proc, object arg0, object arg1, object arg2, object arg3, object arg4) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(null), self, (object)proc, new object[] { arg0, arg1, arg2, arg3, arg4 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, object, TProc, object, object, object, object, object, object>>)callSite).Update(callSite, self, proc, arg0, arg1, arg2, arg3, arg4);
        }
    }

    public sealed class LibraryDispatcherWithScope5 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, RubyScope, object, object, object, object, object, object, object>(Invoke<RubyScope>);
        }

        public object Invoke<TScope>(CallSite/*!*/ callSite, TScope scope, object self, object arg0, object arg1, object arg2, object arg3, object arg4) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, null, new object[] { arg0, arg1, arg2, arg3, arg4 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, object, object, object, object, object, object>>)callSite).Update(callSite, scope, self, arg0, arg1, arg2, arg3, arg4);
        }
    }

    public sealed class LibraryDispatcherWithScopeAndBlock5 : LibraryDispatcher {
        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object, object, object, object, object, object, object>(Invoke<object, object>) :
                (object)new Func<CallSite, RubyScope, object, Proc, object, object, object, object, object, object>(Invoke<RubyScope, Proc>);
        }

        public object Invoke<TScope, TProc>(CallSite/*!*/ callSite, TScope scope, object self, TProc proc, object arg0, object arg1, object arg2, object arg3, object arg4) {
            object result;
            if (IsCurrent(self) && TryInvoke(GetScope(scope), self, (object)proc, new object[] { arg0, arg1, arg2, arg3, arg4 }, out result)) {
                return result;
            }
            return ((CallSite<Func<CallSite, TScope, object, TProc, object, object, object, object, object, object>>)callSite).Update(callSite, scope, self, proc, arg0, arg1, arg2, arg3, arg4);
        }
    }

}

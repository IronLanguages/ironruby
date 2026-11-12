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
using Microsoft.Scripting.Utils;
using System.Diagnostics;
using IronRuby.Builtins;
using System.Collections;

namespace IronRuby.Runtime.Calls {
    using BlockCallTargetUnsplatN = Func<BlockParam, object, object[], RubyArray, object>;

    // L(n, *)
    internal sealed class BlockDispatcherUnsplatN : BlockDispatcherN<BlockCallTargetUnsplatN> {
        internal BlockDispatcherUnsplatN(int parameterCount, BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine)
            : base(parameterCount, attributesAndArity, sourcePath, sourceLine) {
            Debug.Assert(HasUnsplatParameter);
        }

        // R(0, -)
        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg) {
            return InvokeInternal(param, self, procArg, ArrayUtils.EmptyObjects);
        }

        // A block with nothing but the rest parameter (|*args|, the Enumerable helpers' packing
        // block, the prelude's `each { |*values| ... }`) gets the yielded values as a fresh
        // Array built straight from them, rather than from a temporary object[] that was
        // allocated and then copied into one.

        // R(1, -)
        public override object InvokeNoAutoSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1) {
            if (_parameterCount == 0) {
                return _block(param, self, ArrayUtils.EmptyObjects, Rest(RubyOps.MakeArray1(arg1)));
            }
            return InvokeInternal(param, self, procArg, new object[] { arg1 });
        }
        
        // R(1, -)
        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1) {
            if (_parameterCount > 0) {
                IList list = arg1 as IList ?? Protocols.ImplicitTrySplat(param.RubyContext, arg1) ?? new object[] { arg1 }; // TODO: optimize
                return InvokeSplatInternal(param, self, procArg, ArrayUtils.EmptyObjects, list); 
            } else {
                return _block(param, self, ArrayUtils.EmptyObjects, Rest(RubyOps.MakeArray1(arg1)));
            }
        }

        // R(2, -)
        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2) {
            if (_parameterCount == 0) {
                return _block(param, self, ArrayUtils.EmptyObjects, Rest(RubyOps.MakeArray2(arg1, arg2)));
            }
            return InvokeInternal(param, self, procArg, new object[] { arg1, arg2 });
        }

        // R(3, -)
        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3) {
            if (_parameterCount == 0) {
                return _block(param, self, ArrayUtils.EmptyObjects, Rest(RubyOps.MakeArray3(arg1, arg2, arg3)));
            }
            return InvokeInternal(param, self, procArg, new object[] { arg1, arg2, arg3 });
        }

        // R(4, -)
        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, object arg4) {
            if (_parameterCount == 0) {
                return _block(param, self, ArrayUtils.EmptyObjects, Rest(RubyOps.MakeArray4(arg1, arg2, arg3, arg4)));
            }
            return InvokeInternal(param, self, procArg, new object[] { arg1, arg2, arg3, arg4 });
        }

        // R(N, -)
        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args) {
            return InvokeInternal(param, self, procArg, args);
        }

        private object InvokeInternal(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args) {
            // TODO: optimize
            if (args.Length < _parameterCount) {
                // A parameter the caller left out gets nil if it is mandatory and the "use the default"
                // sentinel if it is optional, as in the other dispatchers: |a = 1, *r| called with
                // nothing used to see a nil `a`.
                int supplied = args.Length;
                Array.Resize(ref args, _parameterCount);
                for (int i = supplied; i < args.Length; i++) {
                    args[i] = MissingArg(i);
                }
                return _block(param, self, args, Rest(RubyOps.MakeArray0()));
            } else if (args.Length == _parameterCount) {
                return _block(param, self, args, Rest(RubyOps.MakeArray0()));
            } else if (_parameterCount == 0) {
                return _block(param, self, ArrayUtils.EmptyObjects, Rest(RubyOps.MakeArrayN(args)));
            } else {
                var actualArgs = NewArgs();

                for (int i = 0; i < actualArgs.Length; i++) {
                    actualArgs[i] = args[i];
                }
                
                var array = new RubyArray(args.Length - _parameterCount); 
                for (int i = _parameterCount; i < args.Length; i++) {
                    array.Add(args[i]);
                }

                return _block(param, self, actualArgs, Rest(array));
            }
        }

        // R(0, *)
        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, IList/*!*/ splattee) {
            if (splattee.Count == 1) {
                return Invoke(param, self, procArg, splattee[0]);
            } else {
                return InvokeSplatInternal(param, self, procArg, ArrayUtils.EmptyObjects, splattee);
            }
        }

        // R(1, *)
        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, IList/*!*/ splattee) {
            if (splattee.Count == 0) {
                return Invoke(param, self, procArg, arg1);
            } else {
                return InvokeSplatInternal(param, self, procArg, new object[] { arg1 }, splattee);
            }
        }

        // R(2, *)
        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, IList/*!*/ splattee) {
            return InvokeSplatInternal(param, self, procArg, new object[] { arg1, arg2 }, splattee);
        }

        // R(3, *)
        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, IList/*!*/ splattee) {
            return InvokeSplatInternal(param, self, procArg, new object[] { arg1, arg2, arg3 }, splattee);
        }

        // R(4, *)
        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, object arg4, IList/*!*/ splattee) {
            return InvokeSplatInternal(param, self, procArg, new object[] { arg1, arg2, arg3, arg4 }, splattee);
        }

        // R(N, *)
        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args, IList/*!*/ splattee) {
            return InvokeSplatInternal(param, self, procArg, args, splattee);
        }

        // R(N, *, =)
        public override object InvokeSplatRhs(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args, IList/*!*/ splattee, object rhs) {
            var list = new RubyArray(splattee.Count + 1);
            list.AddRange(splattee);
            list.Add(rhs);
            splattee = list;

            return InvokeSplatInternal(param, self, procArg, args, list);
        }

        private object InvokeSplatInternal(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args, IList/*!*/ splattee) {
            int argsLength = args.Length;

            int nextArg, nextItem;
            CreateArgumentsFromSplattee(_parameterCount, out nextArg, out nextItem, ref args, splattee);

            // parameters neither the arguments nor the splatted items reached (see InvokeInternal):
            for (int i = nextArg; i < _parameterCount; i++) {
                args[i] = MissingArg(i);
            }

            var array = new RubyArray();

            // remaining args:
            while (nextArg < argsLength) {
                array.Add(args[nextArg++]);
            }

            // remaining items:
            while (nextItem < splattee.Count) {
                array.Add(splattee[nextItem++]);
            }

            return _block(param, self, args, Rest(array));
        }
    }
}

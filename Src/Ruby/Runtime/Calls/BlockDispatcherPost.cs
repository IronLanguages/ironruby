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
using System.Collections;
using System.Collections.Generic;
using IronRuby.Builtins;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime.Calls {
    using BlockCallTargetN = Func<BlockParam, object, object[], object>;
    using BlockCallTargetProcN = Func<BlockParam, object, object[], Proc, object>;
    using BlockCallTargetUnsplatN = Func<BlockParam, object, object[], RubyArray, object>;
    using BlockCallTargetUnsplatProcN = Func<BlockParam, object, object[], RubyArray, Proc, object>;

    /// <summary>
    /// A block whose parameter list has post parameters - mandatory ones written after the
    /// optional or rest parameters, as in |a, b = 5, *c, d, e|.
    ///
    /// The other dispatchers hand the arguments to the block body in the order they arrive,
    /// which is right only while the parameters are in that order too. Post parameters are not:
    /// they are bound last, from whatever is left after the leading, optional and rest ones
    /// have taken their share, and the body expects them in the slots between the leading
    /// mandatory and the optional parameters (the order MethodDefinition and BlockDefinition
    /// lay out).
    ///
    /// Every entry point therefore collects the arguments into one list and distributes them,
    /// which is also why this class is not on the fast path of ordinary blocks.
    /// </summary>
    internal abstract class BlockDispatcherPost<T> : BlockDispatcherN<T> where T : class {
        // number of mandatory parameters written after the optional/rest ones
        private readonly int _postCount;

        public BlockDispatcherPost(int parameterCount, int postCount, BlockSignatureAttributes attributesAndArity,
            string sourcePath, int sourceLine)
            : base(parameterCount, attributesAndArity, sourcePath, sourceLine) {
            _postCount = postCount;
        }

        protected abstract object InvokeDistributed(BlockParam/*!*/ param, object self, Proc procArg,
            object[]/*!*/ parameters, RubyArray/*!*/ unsplat);

        /// <summary>
        /// Binds <paramref name="args"/> to the block's parameters: leading mandatory from the
        /// front, then the optional ones for as long as there is more than the post parameters
        /// need, then the rest, and finally the post parameters. Anything a parameter does not
        /// get is nil, or - for an optional one - the sentinel its default expression tests for.
        /// </summary>
        private object InvokeList(BlockParam/*!*/ param, object self, Proc procArg, IList/*!*/ args) {
            var parameters = NewArgs();
            int mandatoryCount = Arity >= 0 ? Arity : -Arity - 1;
            int leadingCount = mandatoryCount - _postCount;
            int optionalCount = _parameterCount - mandatoryCount;
            int next = 0;

            for (int i = 0; i < leadingCount; i++) {
                parameters[i] = next < args.Count ? args[next++] : null;
            }

            for (int i = 0; i < optionalCount; i++) {
                if (args.Count - next <= _postCount) {
                    break;
                }
                parameters[leadingCount + _postCount + i] = args[next++];
            }

            var unsplat = new RubyArray();
            if (HasUnsplatParameter) {
                for (int i = args.Count - next - _postCount; i > 0; i--) {
                    unsplat.Add(args[next++]);
                }
            }

            for (int i = 0; i < _postCount; i++) {
                parameters[leadingCount + i] = next < args.Count ? args[next++] : null;
            }

            return InvokeDistributed(param, self, procArg, parameters, unsplat);
        }

        private static IList/*!*/ Concat(IList/*!*/ args, IList splattee, object rhs, bool hasRhs) {
            var result = new List<object>(args.Count + (splattee != null ? splattee.Count : 0) + (hasRhs ? 1 : 0));
            foreach (var arg in args) result.Add(arg);
            if (splattee != null) foreach (var item in splattee) result.Add(item);
            if (hasRhs) result.Add(rhs);
            return result;
        }

        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg) {
            return InvokeList(param, self, procArg, ArrayUtils.EmptyObjects);
        }

        public override object InvokeNoAutoSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1) {
            return InvokeList(param, self, procArg, new object[] { arg1 });
        }

        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1) {
            // a signature with post parameters always takes more than one parameter, so a single
            // argument is always auto-splatted
            IList list = arg1 as IList ?? Protocols.ImplicitTrySplat(param.RubyContext, arg1) ?? new object[] { arg1 };
            return InvokeList(param, self, procArg, list);
        }

        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2) {
            return InvokeList(param, self, procArg, new object[] { arg1, arg2 });
        }

        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3) {
            return InvokeList(param, self, procArg, new object[] { arg1, arg2, arg3 });
        }

        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, object arg4) {
            return InvokeList(param, self, procArg, new object[] { arg1, arg2, arg3, arg4 });
        }

        public override object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args) {
            return InvokeList(param, self, procArg, args);
        }

        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, IList/*!*/ splattee) {
            return InvokeList(param, self, procArg, splattee);
        }

        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, IList/*!*/ splattee) {
            return InvokeList(param, self, procArg, Concat(new object[] { arg1 }, splattee, null, false));
        }

        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, IList/*!*/ splattee) {
            return InvokeList(param, self, procArg, Concat(new object[] { arg1, arg2 }, splattee, null, false));
        }

        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, IList/*!*/ splattee) {
            return InvokeList(param, self, procArg, Concat(new object[] { arg1, arg2, arg3 }, splattee, null, false));
        }

        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, object arg4, IList/*!*/ splattee) {
            return InvokeList(param, self, procArg, Concat(new object[] { arg1, arg2, arg3, arg4 }, splattee, null, false));
        }

        public override object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args, IList/*!*/ splattee) {
            return InvokeList(param, self, procArg, Concat(args, splattee, null, false));
        }

        public override object InvokeSplatRhs(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args, IList/*!*/ splattee, object rhs) {
            return InvokeList(param, self, procArg, Concat(args, splattee, rhs, true));
        }
    }

    internal sealed class BlockDispatcherPostN : BlockDispatcherPost<BlockCallTargetN> {
        internal BlockDispatcherPostN(int parameterCount, int postCount, BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine)
            : base(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine) {
        }

        protected override object InvokeDistributed(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ parameters, RubyArray/*!*/ unsplat) {
            return _block(param, self, parameters);
        }
    }

    internal sealed class BlockDispatcherPostProcN : BlockDispatcherPost<BlockCallTargetProcN> {
        internal BlockDispatcherPostProcN(int parameterCount, int postCount, BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine)
            : base(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine) {
        }

        protected override object InvokeDistributed(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ parameters, RubyArray/*!*/ unsplat) {
            return _block(param, self, parameters, procArg);
        }
    }

    internal sealed class BlockDispatcherPostUnsplatN : BlockDispatcherPost<BlockCallTargetUnsplatN> {
        internal BlockDispatcherPostUnsplatN(int parameterCount, int postCount, BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine)
            : base(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine) {
        }

        protected override object InvokeDistributed(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ parameters, RubyArray/*!*/ unsplat) {
            return _block(param, self, parameters, unsplat);
        }
    }

    internal sealed class BlockDispatcherPostUnsplatProcN : BlockDispatcherPost<BlockCallTargetUnsplatProcN> {
        internal BlockDispatcherPostUnsplatProcN(int parameterCount, int postCount, BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine)
            : base(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine) {
        }

        protected override object InvokeDistributed(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ parameters, RubyArray/*!*/ unsplat) {
            return _block(param, self, parameters, unsplat, procArg);
        }
    }
}

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

#if FEATURE_CORE_DLR
using System.Linq.Expressions;
#else
using Microsoft.Scripting.Ast;
#endif

using System;
using System.Collections.Generic;
using Microsoft.Scripting;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Interpreter;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Reflection;
using IronRuby.Builtins;
using System.Collections.ObjectModel;
using System.Collections;
using System.Diagnostics;
using System.Threading;

namespace IronRuby.Runtime.Calls {
    using Ast = Expression;
    using AstFactory = IronRuby.Compiler.Ast.AstFactory;

    using BlockCallTarget0 = Func<BlockParam, object, object>;
    using BlockCallTarget1 = Func<BlockParam, object, object, object>;
    using BlockCallTarget2 = Func<BlockParam, object, object, object, object>;
    using BlockCallTarget3 = Func<BlockParam, object, object, object, object, object>;
    using BlockCallTarget4 = Func<BlockParam, object, object, object, object, object, object>;
    using BlockCallTargetN = Func<BlockParam, object, object[], object>;
    using BlockCallTargetProcN = Func<BlockParam, object, object[], Proc, object>;
    using BlockCallTargetUnsplatN = Func<BlockParam, object, object[], RubyArray, object>;
    using BlockCallTargetUnsplatProcN = Func<BlockParam, object, object[], RubyArray, Proc, object>;

    [Flags]
    public enum BlockSignatureAttributes {
        None = 0,

        // {|..., &b|}
        HasProcParameter = 1,

        // {|...,*,...|}
        HasUnsplatParameter = 2,        // TODO: 1.9: arity < 0 iff the block has unsplat => we can remove signature attributes enum

        // bits 31..3 store arity (might be different from formal parameter count)
    }

    internal abstract class BlockDispatcher<T> : BlockDispatcher where T : class {
        protected T _block;

        public BlockDispatcher(BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine)
            : base(attributesAndArity, sourcePath, sourceLine) {
        }

        public override Delegate/*!*/ Method {
            get { return (Delegate)(object)_block; }
        }

        internal override BlockDispatcher/*!*/ SetMethod(object/*!*/ method) {
            // Note: this might potentially be executed by multiple threads. So we can assert that _block == null here.
            // It's ok if the delegate is overwritten multiple times since all the target methods are equivalent.
            _block = (T)method;
            Volatile.Write(ref _hasMethod, true);

            // A block defined in interpreted code gets the interpreter's delegate, which keeps going
            // through LightLambda.RunN - a check and a cast - on every call even once the lambda has
            // compiled itself, and every later Proc of this block reuses that delegate. Call the
            // compiled one directly as soon as there is one: it is the very delegate RunN would call.
            var light = ((Delegate)method).Target as LightLambda;
            if (light != null) {
                light.Compile += OnLightLambdaCompiled;
            }
            return this;
        }

        private void OnLightLambdaCompiled(object sender, LightLambdaCompileEventArgs/*!*/ e) {
            var compiled = e.Compiled as T;
            if (compiled != null) {
                _block = compiled;
            }
        }
    }

    internal abstract class BlockDispatcherN<T> : BlockDispatcher<T> where T : class {
        protected readonly int _parameterCount;

        public override int ParameterCount { get { return _parameterCount; } }

        public BlockDispatcherN(int parameterCount, BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine) 
            : base(attributesAndArity, sourcePath, sourceLine) {
            _parameterCount = parameterCount;
        }

        /// <summary>
        /// A block's argument array, with every optional parameter's slot pre-set to the
        /// sentinel the block body compares against (see
        /// Parameters.TransformOptionalsInitialization).  Leaving those slots null - which
        /// is what a missing *mandatory* parameter gets - made the default expression of
        /// {|a, b = 1|} never run, so b came out nil.
        /// </summary>
        protected object[]/*!*/ NewArgs() {
            var array = new object[_parameterCount];
            int mandatory = Arity >= 0 ? Arity : -Arity - 1;
            for (int i = mandatory; i < array.Length; i++) {
                array[i] = RubyOps.DefaultArgument;
            }
            return array;
        }

        protected object[]/*!*/ MakeArray(object arg1) {
            var array = NewArgs();
            array[0] = arg1;
            return array;
        }

        protected object[]/*!*/ MakeArray(object arg1, object arg2) {
            var array = NewArgs();
            array[0] = arg1;
            array[1] = arg2;
            return array;
        }

        protected object[]/*!*/ MakeArray(object arg1, object arg2, object arg3) {
            var array = NewArgs();
            array[0] = arg1;
            array[1] = arg2;
            array[2] = arg3;
            return array;
        }

        protected object[]/*!*/ MakeArray(object arg1, object arg2, object arg3, object arg4) {
            var array = NewArgs();
            array[0] = arg1;
            array[1] = arg2;
            array[2] = arg3;
            array[3] = arg4;
            return array;
        }
    }

    public abstract class BlockDispatcher {
        // position of the block definition (opening brace):
        private readonly string _sourcePath;
        private readonly int _sourceLine;

        /// <summary>
        /// Set by Proc#ruby2_keywords. It lives on the dispatcher, which a Proc shares with every
        /// copy of itself, so marking one marks them all - before or after the copy was made.
        /// </summary>
        private bool _ruby2Keywords;

        public bool Ruby2Keywords {
            get { return _ruby2Keywords; }
            set { _ruby2Keywords = value; }
        }

        private bool _wrapsMethod;

        /// <summary>
        /// The dispatcher of a Method#to_proc proc. Its source location is the method's, which
        /// Proc#source_location answers, but MRI's Proc#inspect leaves it out for such a proc:
        /// "#&lt;Proc:0x... (lambda)&gt;". On the dispatcher, so every copy of the proc agrees.
        /// </summary>
        public bool WrapsMethod {
            get { return _wrapsMethod; }
            internal set { _wrapsMethod = value; }
        }

        /// <summary>
        /// The array the block's rest parameter is about to be given. See
        /// <see cref="RubyOps.NormalizeRestArgument(RubyArray, bool)"/>.
        /// </summary>
        protected RubyArray/*!*/ Rest(RubyArray/*!*/ array) {
            // Only when the trailing hash is going to stay there: a block that declares keywords
            // of its own has a prologue that takes the hash back off the end, and that prologue
            // can only recognise it while it still says it is keyword arguments.
            var signature = ParameterSignature;
            if (signature != null && !signature.AcceptsRuby2Keywords) {
                return array;
            }
            return RubyOps.NormalizeRestArgument(array, _ruby2Keywords);
        }

        private readonly BlockSignatureAttributes _attributesAndArity;

        // the block's parameter list as it was written; null if the front end did not record one
        private RubyParameterSignature _parameterSignature;

        /// <summary>
        /// What Proc#parameters and Proc#arity report: the declared parameter list rather than
        /// the lowered one the attributes above describe.
        /// </summary>
        public RubyParameterSignature ParameterSignature {
            get { return _parameterSignature; }
            set {
                _parameterSignature = value;
                _checksKeywords = value != null && (value.TakesKeywords || value.RefusesKeywords);
            }
        }

        // the signature declares keywords or **nil, so even a proc's arguments are checked on a yield
        // (see RubyOps.RequireLambdaArity); cached because RefusesKeywords walks the parameter list
        private bool _checksKeywords;

        internal bool ChecksKeywords {
            get { return _checksKeywords; }
        }

        public bool HasUnsplatParameter {
            get { return (_attributesAndArity & BlockSignatureAttributes.HasUnsplatParameter) != 0; }
        }

        public bool HasProcParameter {
            get { return (_attributesAndArity & BlockSignatureAttributes.HasProcParameter) != 0; }
        }

        public int Arity {
            get { return ((int)_attributesAndArity >> 2); }
        }

        /// <summary>
        /// What a parameter that the caller did not supply should receive: nil for a
        /// mandatory one, and the sentinel the block body tests against for an optional
        /// one, so that {|a, b = 1|} actually evaluates the default.
        /// </summary>
        protected object MissingArg(int index) {
            int mandatory = Arity >= 0 ? Arity : -Arity - 1;
            return index >= mandatory ? RubyOps.DefaultArgument : null;
        }

        public static BlockSignatureAttributes MakeAttributes(BlockSignatureAttributes attributes, int arity) {
            return attributes | (BlockSignatureAttributes)(arity << 2);
        }

        // Doesn't include unsplat parameter. 
        // Includes anonymous parameter.
        public abstract int ParameterCount { get; }
        public abstract Delegate/*!*/ Method { get; }

        // Method != null, without the virtual call: InstantiateBlock asks on every call with a literal block
        private protected bool _hasMethod;

        internal bool HasMethod {
            get { return Volatile.Read(ref _hasMethod); }
        }
        internal abstract BlockDispatcher/*!*/ SetMethod(object/*!*/ method);

        public string SourcePath { get { return _sourcePath; } }
        public int SourceLine { get { return _sourceLine; } }

        public abstract object Invoke(BlockParam/*!*/ param, object self, Proc procArg);
        public abstract object InvokeNoAutoSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1);
        public abstract object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1);
        public abstract object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2);
        public abstract object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3);
        public abstract object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, object arg4);
        public abstract object Invoke(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args);

        public abstract object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, IList/*!*/ splattee);
        public abstract object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, IList/*!*/ splattee);
        public abstract object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, IList/*!*/ splattee);
        public abstract object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, IList/*!*/ splattee);
        public abstract object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object arg1, object arg2, object arg3, object arg4, IList/*!*/ splattee);
        public abstract object InvokeSplat(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args, IList/*!*/ splattee);

        public abstract object InvokeSplatRhs(BlockParam/*!*/ param, object self, Proc procArg, object[]/*!*/ args, IList/*!*/ splattee, object rhs);

        internal const int MaxBlockArity = 4;
        internal const int HiddenParameterCount = 2;

        internal BlockDispatcher(BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine) {
            _attributesAndArity = attributesAndArity;
            _sourcePath = sourcePath;
            _sourceLine = sourceLine;
        }

        internal static BlockDispatcher/*!*/ Create(int parameterCount, BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine) {
            return Create(parameterCount, 0, attributesAndArity, sourcePath, sourceLine);
        }

        internal static BlockDispatcher/*!*/ Create(int parameterCount, int postCount, BlockSignatureAttributes attributesAndArity, string sourcePath, int sourceLine) {
            if (postCount > 0) {
                // post parameters are bound out of order, so they need a dispatcher that knows
                // how many there are; it always passes the parameters in an array
                if ((attributesAndArity & BlockSignatureAttributes.HasUnsplatParameter) == 0) {
                    return ((attributesAndArity & BlockSignatureAttributes.HasProcParameter) == 0)
                        ? (BlockDispatcher)new BlockDispatcherPostN(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine)
                        : new BlockDispatcherPostProcN(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine);
                } else {
                    return ((attributesAndArity & BlockSignatureAttributes.HasProcParameter) == 0)
                        ? (BlockDispatcher)new BlockDispatcherPostUnsplatN(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine)
                        : new BlockDispatcherPostUnsplatProcN(parameterCount, postCount, attributesAndArity, sourcePath, sourceLine);
                }
            }

            if ((attributesAndArity & BlockSignatureAttributes.HasUnsplatParameter) == 0) {
                if ((attributesAndArity & BlockSignatureAttributes.HasProcParameter) == 0) {
                    switch (parameterCount) {
                        case 0: return new BlockDispatcher0(attributesAndArity, sourcePath, sourceLine);
                        case 1: return new BlockDispatcher1(attributesAndArity, sourcePath, sourceLine);
                        case 2: return new BlockDispatcher2(attributesAndArity, sourcePath, sourceLine);
                        case 3: return new BlockDispatcher3(attributesAndArity, sourcePath, sourceLine);
                        case 4: return new BlockDispatcher4(attributesAndArity, sourcePath, sourceLine);
                        default: return new BlockDispatcherN(parameterCount, attributesAndArity, sourcePath, sourceLine);
                    }
                } else {
                    return new BlockDispatcherProcN(parameterCount, attributesAndArity, sourcePath, sourceLine);
                }
            } else {
                if ((attributesAndArity & BlockSignatureAttributes.HasProcParameter) == 0) {
                    return new BlockDispatcherUnsplatN(parameterCount, attributesAndArity, sourcePath, sourceLine);
                } else {
                    return new BlockDispatcherUnsplatProcN(parameterCount, attributesAndArity, sourcePath, sourceLine);
                }
            }
        }

        internal static LambdaExpression/*!*/ CreateLambda(Expression body, string name, ICollection<ParameterExpression> parameters,
            int parameterCount, BlockSignatureAttributes attributes) {
            return CreateLambda(body, name, parameters, parameterCount, 0, attributes);
        }

        internal static LambdaExpression/*!*/ CreateLambda(Expression body, string name, ICollection<ParameterExpression> parameters,
            int parameterCount, int postCount, BlockSignatureAttributes attributes) {

            if ((attributes & BlockSignatureAttributes.HasUnsplatParameter) == 0) {
                if ((attributes & BlockSignatureAttributes.HasProcParameter) == 0) {
                    // a post-parameter dispatcher always passes an array, whatever the count
                    switch (postCount > 0 ? Int32.MaxValue : parameterCount) {
                        case 0: return Ast.Lambda<BlockCallTarget0>(body, name, parameters);
                        case 1: return Ast.Lambda<BlockCallTarget1>(body, name, parameters);
                        case 2: return Ast.Lambda<BlockCallTarget2>(body, name, parameters);
                        case 3: return Ast.Lambda<BlockCallTarget3>(body, name, parameters);
                        case 4: return Ast.Lambda<BlockCallTarget4>(body, name, parameters);
                        default: return Ast.Lambda<BlockCallTargetN>(body, name, parameters);
                    }
                } else {
                    return Ast.Lambda<BlockCallTargetProcN>(body, name, parameters);
                }
            } else {
                if ((attributes & BlockSignatureAttributes.HasProcParameter) == 0) {
                    return Ast.Lambda<BlockCallTargetUnsplatN>(body, name, parameters);
                } else {
                    return Ast.Lambda<BlockCallTargetUnsplatProcN>(body, name, parameters);
                }
            }
        }

        private static void CopyArgumentsFromSplattee(object[]/*!*/ args, int initializedArgCount, int parameterCount,
            out int nextArg, out int nextItem, IList/*!*/ splattee) {

            int i = Math.Min(initializedArgCount, parameterCount);
            int j = 0;
            while (i < parameterCount && j < splattee.Count) {
                args[i++] = splattee[j++];
            }
        
            nextArg = i;
            nextItem = j;
        }

        // Expects first "initializeArgCount" slots of "args" array initialized with actual argument values 
        // and fills the rest by splatting "splattee". The size of the array "args" is the number of formal parameters the block takes.
        internal static object[]/*!*/ CopyArgumentsFromSplattee(object[]/*!*/ args, int initializedArgCount, IList/*!*/ splattee) {
            int nextArg, nextItem;
            CopyArgumentsFromSplattee(args, initializedArgCount, args.Length, out nextArg, out nextItem, splattee);
            return args;
        }

        internal static void CreateArgumentsFromSplattee(int parameterCount, out int nextArg, out int nextItem, ref object[]/*!*/ args, IList/*!*/ splattee) {
            // the args array is passed to the block, we need at least space for all explicit parameters:
            int originalLength = args.Length;
            if (args.Length < parameterCount) {
                Array.Resize(ref args, parameterCount);
            }

            CopyArgumentsFromSplattee(args, originalLength, parameterCount, out nextArg, out nextItem, splattee);
        }

        internal static object[]/*!*/ CreateArgumentsFromSplatteeAndRhs(int parameterCount, object[]/*!*/ args, IList/*!*/ splattee, object rhs) {
            int nextArg, nextItem;

            // the args array is passed to the block, we need at least space for all explicit parameters:
            CreateArgumentsFromSplattee(parameterCount, out nextArg, out nextItem, ref args, splattee);

            if (nextArg < args.Length) {
                args[nextArg++] = rhs;
            }

            return args;
        }
    }
}
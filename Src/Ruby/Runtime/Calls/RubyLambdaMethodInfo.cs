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
using System.Threading;
using System.Reflection;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Compiler;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using Ast = System.Linq.Expressions.Expression;

namespace IronRuby.Runtime.Calls {

    public class RubyLambdaMethodInfo : RubyMemberInfo {
        private static int _Id = 1;

        // _id names this entry for super call sites (RubyOps.GetSuperCallTarget), so every copy
        // gets its own; _bodyId is shared by the copies `alias' and visibility changes make, which
        // are the same method to Method#==.
        private readonly int _id;
        private readonly int _bodyId;
        private readonly Proc/*!*/ _lambda;
        private readonly string/*!*/ _definitionName;

        // The block the method was defined with, and the Ractor that defined it (null: the main
        // Ractor): only a shareable block may run in another Ractor - unless the block is the core
        // library's (RubyStackTraceBuilder.IsInternalFile), which stands in for CRuby's C methods.
        private readonly Proc/*!*/ _block;
        private readonly RactorLocals _definedRactor;
        private readonly bool _isCoreLibrary;

        internal RubyLambdaMethodInfo(Proc/*!*/ block, string/*!*/ definitionName, RubyMemberFlags flags, RubyModule/*!*/ declaringModule)
            : this(block, definitionName, flags, declaringModule, 0, CurrentRactor(declaringModule.Context)) {
        }

        private RubyLambdaMethodInfo(Proc/*!*/ block, string/*!*/ definitionName, RubyMemberFlags flags, RubyModule/*!*/ declaringModule, int bodyId,
            RactorLocals definedRactor)
            : base(flags, declaringModule) {
            Assert.NotNull(block, definitionName, declaringModule);
            _lambda = block.ToLambda(this);
            _block = block;
            _definedRactor = definedRactor;
            _isCoreLibrary = RubyStackTraceBuilder.IsInternalFile(block.Dispatcher.SourcePath);
            _definitionName = definitionName;
            _id = Interlocked.Increment(ref _Id);
            _bodyId = bodyId != 0 ? bodyId : _id;
        }

        private static RactorLocals CurrentRactor(RubyContext/*!*/ context) {
            return context.RactorsStarted ? RactorIsolation.GetNonMain(context) : null;
        }

        /// <summary>
        /// A call of the method in a runtime that has started Ractors: one defined with a block
        /// that is not shareable runs only in the Ractor that defined it (CRuby's bmethod check).
        /// </summary>
        public static void CheckRactor(RubyLambdaMethodInfo/*!*/ method) {
            var context = method.DeclaringModule.Context;
            if (!method._isCoreLibrary && RactorIsolation.GetNonMain(context) != method._definedRactor &&
                !RactorIsolation.IsShareable(context, method._block)) {
                throw new RuntimeError("defined with an un-shareable Proc in a different Ractor");
            }
        }

        public override RubyParameterSignature GetParameterSignature() {
            return _lambda.Dispatcher.ParameterSignature;
        }

        /// <summary>
        /// Module#ruby2_keywords on a method made by define_method. The flag goes on the block's
        /// dispatcher, which is what fills the rest parameter when the method runs.
        /// </summary>
        public bool TrySetRuby2Keywords() {
            var signature = _lambda.Dispatcher.ParameterSignature;
            if (signature == null || !signature.AcceptsRuby2Keywords) {
                return false;
            }

            _lambda.Dispatcher.Ruby2Keywords = true;
            return true;
        }

        public override int GetArity() {
            var signature = _lambda.Dispatcher.ParameterSignature;
            // define_method turns the block into a method, and a method's parameters bind the
            // way a lambda's do, so the block reports itself as a lambda here
            return signature != null ? signature.GetArity(true) : _lambda.Dispatcher.Arity;
        }

        public override RubyArray/*!*/ GetRubyParameterArray() {
            var signature = _lambda.Dispatcher.ParameterSignature;
            return signature != null ? signature.GetParameterArray(Context, true) : base.GetRubyParameterArray();
        }

        public Proc/*!*/ Lambda {
            get { return _lambda; }
        }

        internal int Id {
            get { return _id; }
        }

        public override bool IsEquivalentTo(RubyMemberInfo/*!*/ other) {
            if (ReferenceEquals(this, other)) {
                return true;
            }
            var info = other as RubyLambdaMethodInfo;
            return info != null && info._bodyId == _bodyId;
        }

        public override int GetEquivalenceHashCode() {
            return _bodyId;
        }

        public string/*!*/ DefinitionName {
            get { return _definitionName; }
        }

        public override MemberInfo/*!*/[]/*!*/ GetMembers() {
            return new MemberInfo[] { _lambda.Dispatcher.Method.GetMethodInfo() };
        }

        protected internal override RubyMemberInfo/*!*/ Copy(RubyMemberFlags flags, RubyModule/*!*/ module) {
            return new RubyLambdaMethodInfo(_block, _definitionName, flags, module, _bodyId, _definedRactor);
        }

        public override RubyMemberInfo TrySelectOverload(Type/*!*/[]/*!*/ parameterTypes) {
            return parameterTypes.Length == _lambda.Dispatcher.ParameterCount 
                && CollectionUtils.TrueForAll(parameterTypes, (type) => type == typeof(object)) ? this : null;
        }

        internal override void BuildCallNoFlow(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ name) {
            // A block turned into a method by define_method takes its arguments the way a
            // method does, not the way a block does: too few or too many is an error rather
            // than nil-padding or dropping.  (A splatted call cannot be counted here, so it
            // keeps the old lenient behaviour.)
            if (!args.Signature.HasSplattedArgument) {
                // An assignment call carries its right-hand side in a slot of its own, which
                // ArgumentCount does not count: o.x = 1 arrives here looking like no arguments
                // at all, and o[1] = 2 like one.
                int actual = args.Signature.ArgumentCount + (args.Signature.HasRhsArgument ? 1 : 0);
                int arity = _lambda.Dispatcher.Arity;
                int mandatory = arity >= 0 ? arity : -arity - 1;
                int maximum = _lambda.Dispatcher.HasUnsplatParameter ? Int32.MaxValue : _lambda.Dispatcher.ParameterCount;

                // { |a,| } is compiled with a hidden unsplat parameter, yet as a method it takes exactly one argument:
                var signature = _lambda.Dispatcher.ParameterSignature;
                if (maximum == Int32.MaxValue && signature != null && signature.MaxArgumentCount >= 0) {
                    maximum = signature.MaxArgumentCount;
                }

                if (actual < mandatory || actual > maximum) {
                    if (mandatory == maximum) {
                        metaBuilder.SetWrongNumberOfArgumentsError(actual, mandatory);
                    } else {
                        metaBuilder.SetError(Methods.MakeWrongNumberOfArgumentsErrorN.OpCall(
                            AstUtils.Constant(actual),
                            AstUtils.Constant(maximum == Int32.MaxValue
                                ? mandatory.ToString(System.Globalization.CultureInfo.InvariantCulture) + "+"
                                : mandatory.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".." +
                                  maximum.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        ));
                    }
                    return;
                }
            }

            if (args.Signature.HasBlock) {
                // The call is the flow-control frame of a literal block given to it, as a def method's is (its
                // RubyMethodScope): a break from the block, wherever the body passes it on to, ends this call.
                metaBuilder.ControlFlowBuilder = RubyMethodInfo.RuleControlFlowBuilder;
            }

            Proc.BuildCall(
                metaBuilder,
                AstUtils.Constant(_lambda),            // proc object
                args.TargetExpression,                 // self
                args
            );

            // once there are Ractors, a block that is not shareable runs only in the Ractor that defined the method
            metaBuilder.AddInitialization(Ast.IfThen(
                Ast.Property(AstUtils.Constant(args.RubyContext), typeof(RubyContext).GetProperty("RactorsStarted")),
                Ast.Call(typeof(RubyLambdaMethodInfo).GetMethod("CheckRactor"), AstUtils.Constant(this))
            ));
        }
    }
}

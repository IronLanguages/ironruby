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
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Dynamic;
using System.Threading;
using Microsoft.Scripting;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Actions.Calls;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Compiler.Generation;
using IronRuby.Compiler;

using AstFactory = IronRuby.Compiler.Ast.AstFactory;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Calls {
    using Ast = Expression;
    
    /// <summary>
    /// Performs method binding for calling CLR methods.
    /// Currently this is used for all builtin libary methods and interop calls to CLR methods
    /// </summary>
    public sealed class RubyLibraryMethodInfo : RubyMethodGroupBase {
        private readonly LibraryOverload/*!*/[]/*!*/ _overloads;

        /// <summary>
        /// Creates a Ruby method implemented by a method group of CLR methods.
        /// </summary>
        internal RubyLibraryMethodInfo(LibraryOverload/*!*/[]/*!*/ overloads, RubyMemberFlags flags, RubyModule/*!*/ declaringModule)
            : base(null, flags, declaringModule) {
            Assert.NotNullItems(overloads);
            Assert.NotEmpty(overloads);
            _overloads = overloads;
        }

        public RubyLibraryMethodInfo(LibraryOverload/*!*/[]/*!*/ overloads, RubyMethodVisibility visibility, RubyModule/*!*/ declaringModule) 
            : this(overloads, (RubyMemberFlags)visibility & RubyMemberFlags.VisibilityMask, declaringModule) {
            ContractUtils.RequiresNotNull(declaringModule, "declaringModule");
            ContractUtils.RequiresNotNullItems(overloads, "overloads");
        }

        // copy ctor
        private RubyLibraryMethodInfo(RubyLibraryMethodInfo/*!*/ info, OverloadInfo/*!*/[]/*!*/ methods)
            : base(methods, info.Flags, info.DeclaringModule) {
        }

        internal LibraryOverload/*!*/[]/*!*/ Overloads {
            get { return _overloads; }
        }

        internal override SelfCallConvention CallConvention {
            get { return SelfCallConvention.SelfIsParameter; }
        }

        internal override bool ImplicitProtocolConversions {
            get { return false; }
        }

        internal protected override OverloadInfo/*!*/[]/*!*/ MethodBases {
            get {
                Debug.Assert(base.MethodBases != null || _overloads != null);

                // don't need to lock MethodBases since all values calculated by multiple threads are the same: 
                return base.MethodBases ?? SetMethodBasesNoLock(_overloads);
            }
        }

        // 0 = not computed yet, 1 = no, 2 = yes
        private int _notImplemented;

        /// <summary>
        /// True if every overload is marked [RubyNotImplemented] - the method exists but can only raise
        /// NotImplementedError, so #respond_to? denies it.
        /// </summary>
        public bool IsNotImplemented {
            get {
                if (_notImplemented == 0) {
                    bool result = true;
                    foreach (var member in GetMembers()) {
                        if (!member.IsDefined(typeof(RubyNotImplementedAttribute), false)) {
                            result = false;
                            break;
                        }
                    }
                    _notImplemented = result ? 2 : 1;
                }
                return _notImplemented == 2;
            }
        }

        // 0 = not computed yet, 1 = no, 2 = yes
        private int _emptyUnary;

        /// <summary>
        /// True for a method that does nothing and takes exactly one argument: Module#method_added and the other
        /// default hooks (see <see cref="RubyContext.IsDefaultHook"/>, which asks for every hook it would call).
        /// </summary>
        internal bool IsEmptyUnary {
            get {
                if (_emptyUnary == 0) {
                    _emptyUnary = (IsEmpty && GetArity() == 1) ? 2 : 1;
                }
                return _emptyUnary == 2;
            }
        }

        public override MemberInfo/*!*/[]/*!*/ GetMembers() {
            return ArrayUtils.ConvertAll(MethodBases, (o) => o.ReflectionInfo);
        }

        protected internal override RubyMemberInfo/*!*/ Copy(RubyMemberFlags flags, RubyModule/*!*/ module) {
            return new RubyLibraryMethodInfo(_overloads, flags, module);
        }

        protected override RubyMemberInfo/*!*/ Copy(OverloadInfo/*!*/[]/*!*/ methods) {
            return new RubyLibraryMethodInfo(this, methods);
        }

        internal override MemberDispatcher GetDispatcher(Type/*!*/ delegateType, RubyCallSignature signature, object target, int version) {
            if (!(target is IRubyObject)) {
                return null;
            }

            int arity;
            if (!IsEmpty || (arity = GetArity()) != 1) {
                return null;
            }

            // The stub reports no :c_call, but CRuby reports these methods - Module#method_added and the other default
            // hooks are C functions - so a rule bound while c_call is traced must not be the stub. Switching c_call
            // tracing on or off rebinds every rule.
            if (TracePoint.CCallTracing) {
                return null;
            }

            return MethodDispatcher.CreateRubyObjectDispatcher(
                delegateType, new Func<object, Proc, object, object>(EmptyRubyMethodStub1), arity, signature.HasScope, signature.HasBlock, version
            );
        }

        public static object EmptyRubyMethodStub1(object self, Proc block, object arg0) {
            // nop
            return null;
        }

        #region Calls without rules (LibraryDispatcher)

        // Per overload: null until made, NoCallPlan if a call of the overload can't be made without a rule.
        private static readonly object NoCallPlan = new object();
        private object[] _callPlanStates;

        // The overload the resolver picked for calls of the method of several overloads, by the types they were made with.
        private Dictionary<LibraryResolutionKey, int> _resolvedOverloads;

        internal LibraryCallPlan GetCallPlan(int index) {
            var states = _callPlanStates;
            if (states == null) {
                Interlocked.CompareExchange(ref _callPlanStates, new object[MethodBases.Length], null);
                states = _callPlanStates;
            }

            var state = states[index];
            if (state == null) {
                state = (object)LibraryCallPlan.TryCreate(Context, MethodBases[index]) ?? NoCallPlan;
                Interlocked.CompareExchange(ref states[index], state, null);
                state = states[index];
            }
            return state as LibraryCallPlan;
        }

        // null until made; NoSelector if the overloads' parameters can't be told
        private object _overloadSelector;
        private static readonly object NoSelector = new object();

        internal LibraryOverloadSelector GetOverloadSelector() {
            var selector = _overloadSelector;
            if (selector == null) {
                Interlocked.CompareExchange(ref _overloadSelector, (object)LibraryOverloadSelector.TryCreate(MethodBases, CallConvention) ?? NoSelector, null);
                selector = _overloadSelector;
            }
            return selector as LibraryOverloadSelector;
        }

        internal int GetResolvedOverload(RubyCallAction/*!*/ binder, object target, Type/*!*/[]/*!*/ argTypes, bool hasBlock, bool blockGiven,
            object[]/*!*/ args) {

            var key = new LibraryResolutionKey(target != null ? target.GetType() : null, argTypes, hasBlock, blockGiven);
            int index;
            lock (this) {
                if (_resolvedOverloads != null && _resolvedOverloads.TryGetValue(key, out index)) {
                    return index;
                }
            }

            index = LibraryDispatcher.Resolve(binder, this, args);

            lock (this) {
                if (_resolvedOverloads == null) {
                    _resolvedOverloads = new Dictionary<LibraryResolutionKey, int>();
                }
                _resolvedOverloads[key] = index;
            }
            return index;
        }

        #endregion

        internal override void BuildCallNoFlow(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ name) {
            BuildCallNoFlow(metaBuilder, args, name, MethodBases, CallConvention, ImplicitProtocolConversions);

            // Rules are rebound when c_call tracing is switched on or off, so a rule bound without it costs nothing.
            if (TracePoint.CCallTracing && !metaBuilder.Error && metaBuilder.Result != null) {
                metaBuilder.Result = AddCallTracing(metaBuilder.Result, args, name);
            }
        }

        private Expression/*!*/ AddCallTracing(Expression/*!*/ call, CallArguments/*!*/ args, string/*!*/ name) {
            Expression scope = AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope));
            Expression target = AstUtils.Convert(args.TargetExpression, typeof(object));
            Expression method = AstUtils.Constant(this, typeof(RubyMemberInfo));
            Expression methodName = AstUtils.Constant(name);
            Expression before = Methods.TraceLibraryCall.OpCall(scope, target, method, methodName);

            if (call.Type == typeof(void)) {
                return Ast.Block(before, call, Methods.TraceLibraryReturn.OpCall(scope, target, method, methodName, AstUtils.Constant(null)));
            }

            var result = Ast.Variable(call.Type, "#result");
            return Ast.Block(new[] { result },
                before,
                Ast.Assign(result, call),
                Methods.TraceLibraryReturn.OpCall(scope, target, method, methodName, AstUtils.Convert(result, typeof(object))),
                result
            );
        }
    }
}


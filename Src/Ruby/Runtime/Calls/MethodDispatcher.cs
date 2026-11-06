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

using System.Linq.Expressions;

using System;
using System.Collections.Generic;
using System.Text;
using IronRuby.Builtins;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;
using System.Collections.ObjectModel;
using Microsoft.Scripting.Interpreter;
using Microsoft.Scripting;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using IronRuby.Compiler;

namespace IronRuby.Runtime.Calls {
    using Ast = Expression;

    public abstract partial class MemberDispatcher {
        private static readonly object[]/*!*/ _boxedSmallInts = CreateBoxedSmallInts();

        private static object[]/*!*/ CreateBoxedSmallInts() {
            var result = new object[128 + 1024];
            for (int i = 0; i < result.Length; i++) {
                result[i] = Microsoft.Scripting.Runtime.ScriptingRuntimeHelpers.Int32ToObject(i - 128);
            }
            return result;
        }

        /// <summary>
        /// An argument as the method delegate takes it, an object. A precompiled dispatcher's site is
        /// typed by what it was first called with, so a literal 1 arrives as an Int32, and handing it on
        /// boxed it into a new object on every call - a sixth of the cost of calling a one-argument Ruby
        /// method. An Integer in -128..1023 comes from a cache of boxed ones instead (filled from the DLR's,
        /// so they are the same objects the rest of the runtime hands out), and a Boolean is one of the
        /// two shared ones. The type tests are constants per instantiation, so for any other T this is
        /// the plain conversion.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static object Box<T>(T value) {
            if (typeof(T) == typeof(int)) {
                int i = Unsafe.As<T, int>(ref value);
                object[] cache = _boxedSmallInts;
                return ((uint)(i + 128) < (uint)cache.Length) ? cache[i + 128] : (object)i;
            }
            if (typeof(T) == typeof(bool)) {
                return Microsoft.Scripting.Runtime.ScriptingRuntimeHelpers.BooleanToObject(Unsafe.As<T, bool>(ref value));
            }
            return value;
        }

        internal int Version;

        // A receiver that is not a Ruby object: nil (DynamicNull), true/false or a CLR object - its exact type, the version of its
        // class and, for true and false, the value (see MetaObjectBuilder.AddTargetTypeTest). Null for a Ruby object receiver.
        private Type _clrType;
        private VersionHandle _classVersion;
        private bool _boolValue;

        // The type arguments of the delegate types of call sites. Type.GetGenericArguments allocates a fresh array each time, and
        // a site's delegate type is asked for at every bind of a precompiled dispatcher; there are a few dozen such types.
        private static readonly Dictionary<Type, Type[]>/*!*/ _delegateTypeArguments = new Dictionary<Type, Type[]>();

        /// <summary>
        /// The generic arguments of <paramref name="delegateType"/>, an empty array if it has none. The array is shared: don't change it.
        /// </summary>
        internal static Type/*!*/[]/*!*/ GetDelegateTypeArguments(Type/*!*/ delegateType) {
            Type[] result;
            lock (_delegateTypeArguments) {
                if (_delegateTypeArguments.TryGetValue(delegateType, out result)) {
                    return result;
                }
            }
            result = delegateType.IsGenericType ? delegateType.GetGenericArguments() : Type.EmptyTypes;
            lock (_delegateTypeArguments) {
                _delegateTypeArguments[delegateType] = result;
            }
            return result;
        }

        // the type of a Ruby method's body taking count arguments, see CreateRubyMethodLambda
        internal static Type/*!*/ GetRubyMethodDelegateType(int count) {
            switch (count) {
                case 0: return typeof(Func<object, Proc, object>);
                case 1: return typeof(Func<object, Proc, object, object>);
                case 2: return typeof(Func<object, Proc, object, object, object>);
                case 3: return typeof(Func<object, Proc, object, object, object, object>);
                case 4: return typeof(Func<object, Proc, object, object, object, object, object>);
                case 5: return typeof(Func<object, Proc, object, object, object, object, object, object>);
                default: return null;
            }
        }

        internal void SetClrReceiver(object target, RubyClass/*!*/ targetClass) {
            _clrType = (target != null) ? target.GetType() : typeof(Microsoft.Scripting.Runtime.DynamicNull);
            _boolValue = target is bool && (bool)target;
            _classVersion = targetClass.Version;
        }

        public abstract object/*!*/ CreateDelegate(bool isUntyped);

        /// <summary>
        /// The guard a precompiled dispatcher runs on every call: the receiver is a Ruby object
        /// whose class has not had a method table change since the dispatcher was made.
        /// RubyObject is the class of nearly every such receiver, and an exact type test is two
        /// instructions inline, where the IRubyObject cast it saves is a call to the runtime's
        /// interface-cast helper. Any other IRubyObject goes the old way.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsCurrentFor(object self) {
            if (self != null) {
                Type type = self.GetType();
                if (type == typeof(RubyObject)) {
                    return Unsafe.As<RubyObject>(self).ImmediateClass.Version.Method == Version;
                }
                // a class or module receiver - Foo.new, Math.sqrt, any class method:
                if (type == typeof(RubyClass) || type == typeof(RubyModule)) {
                    return Unsafe.As<RubyModule>(self).ImmediateClass.Version.Method == Version;
                }
                if (type == _clrType) {
                    return _classVersion.Method == Version && (type != typeof(bool) || (bool)self == _boolValue);
                }
            } else if (_clrType == typeof(Microsoft.Scripting.Runtime.DynamicNull)) {
                return _classVersion.Method == Version;
            }
            IRubyObject obj = self as IRubyObject;
            return obj != null && obj.ImmediateClass.Version.Method == Version;
        }

        internal static object CreateDispatcher(Type/*!*/ func, int mandatoryParamCount, bool hasScope, bool hasBlock, int version,
            Func<MethodDispatcher> parameterlessFactory, Type[] genericFactories) {
            Type[] funcArgs = GetDelegateTypeArguments(func);

            // Func<CallSite, (RubyScope)?, TSelf, (Proc)?, T1, ... TN, object>
            int selfIndex = 1 + (hasScope ? 1 : 0);
            int firstParameterIndex = selfIndex + 1 + (hasBlock ? 1 : 0);
            int parameterCount = funcArgs.Length - firstParameterIndex - 1;

            // invalid number of arguments passed to the site:
            if (parameterCount != mandatoryParamCount) {
                return null;
            }

            if (parameterCount > MethodDispatcher.MaxPrecompiledArity) {
                return null;
            }

            // self must be an object:
            if (funcArgs[selfIndex] != typeof(object)) {
                return null;
            }

            if (parameterCount == 0) {
                return parameterlessFactory();
            }

            // TODO: cache?
            // remove "self":
            var types = funcArgs.GetSlice(firstParameterIndex, parameterCount);

            // Without dynamic code (NativeAOT) only the instantiations the ahead-of-time compiler
            // saw exist, and over a value type that is rarely the case: build a rule instead.
            if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported && Array.Exists(types, t => t.IsValueType)) {
                return null;
            }
            return Activator.CreateInstance(genericFactories[parameterCount - 1].MakeGenericType(types));
        }
    }

    public abstract class MethodDispatcher : MemberDispatcher {
        internal static MethodDispatcher CreateRubyObjectDispatcher(Type/*!*/ func, Delegate/*!*/ method, int mandatoryParamCount, 
            bool hasScope, bool hasBlock, int version) {

            var dispatcher = (MethodDispatcher)CreateDispatcher(func, mandatoryParamCount, hasScope, hasBlock, version,
                () => 
                hasScope ?
                    (hasBlock ? (MethodDispatcher)new RubyObjectMethodDispatcherWithScopeAndBlock() : new RubyObjectMethodDispatcherWithScope()) :
                    (hasBlock ? (MethodDispatcher)new RubyObjectMethodDispatcherWithBlock() : new RubyObjectMethodDispatcher()),
                hasScope ? 
                    (hasBlock ? RubyObjectMethodDispatchersWithScopeAndBlock : RubyObjectMethodDispatchersWithScope) :
                    (hasBlock ? RubyObjectMethodDispatchersWithBlock : RubyObjectMethodDispatchers)
            );

            if (dispatcher != null) {
                dispatcher.Initialize(method, version);
            }
            return dispatcher;
        }

        /// <summary>
        /// A dispatcher for a call a refinement could change, made from the scope of a method or of a block written
        /// directly in one (<paramref name="inBlock"/>): it also tests that the method's refinement record holds
        /// <paramref name="refinements"/>, as the rule would (see RubyCallAction.BindPrecompiled).
        /// </summary>
        internal static MethodDispatcher CreateRefinedRubyObjectDispatcher(Type/*!*/ func, Delegate/*!*/ method, int mandatoryParamCount,
            bool hasBlock, int version, RefinementActivation/*!*/ refinements, bool inBlock) {

            var dispatcher = (MethodDispatcher)CreateDispatcher(func, mandatoryParamCount, true, hasBlock, version,
                () => hasBlock ? (MethodDispatcher)new RefinedRubyObjectMethodDispatcherWithScopeAndBlock() : new RefinedRubyObjectMethodDispatcherWithScope(),
                hasBlock ? RefinedRubyObjectMethodDispatchersWithScopeAndBlock : RefinedRubyObjectMethodDispatchersWithScope
            );

            if (dispatcher != null) {
                dispatcher.Initialize(method, version);
                dispatcher.InitializeRefinements(refinements, inBlock);
            }
            return dispatcher;
        }

        internal abstract void Initialize(Delegate/*!*/ method, int version);

        internal virtual void InitializeRefinements(RefinementActivation/*!*/ refinements, bool inBlock) {
            throw Assert.Unreachable;
        }

        /// <summary>
        /// Points the dispatcher at the compiled form of the body it was made for (see
        /// RubyMethodBody.RegisterDispatcher). Either delegate runs the same body, so a call racing
        /// with this may use either one.
        /// </summary>
        internal abstract void SetMethod(Delegate/*!*/ method);
    }

    public abstract class MethodDispatcher<TRubyFunc> : MethodDispatcher {
        internal TRubyFunc Method;

        internal override void Initialize(Delegate/*!*/ method, int version) {
            Assert.NotNull(method);
            Method = (TRubyFunc)(object)method;
            Version = version;
        }

        internal override void SetMethod(Delegate/*!*/ method) {
            if (method is TRubyFunc typed) {
                Method = typed;
            }
        }
    }

    /// <summary>
    /// The base of the dispatchers of a call a refinement could change (see CreateRefinedRubyObjectDispatcher). The scope of a
    /// method has the refinements its `def' recorded, and so does that of a block written directly in one while no refine
    /// block is running (RubyCallAction.MakeRefinementGuard tests the same in a rule).
    /// </summary>
    public abstract class RefinedMethodDispatcher<TRubyFunc> : MethodDispatcher<TRubyFunc> {
        private RefinementActivation _refinements;
        private bool _inBlock;

        internal override void InitializeRefinements(RefinementActivation/*!*/ refinements, bool inBlock) {
            Assert.NotNull(refinements);
            _refinements = refinements;
            _inBlock = inBlock;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsCurrentFor(object self, object scope) {
            return IsCurrentFor(self) && HasRefinements(scope);
        }

        private bool HasRefinements(object scope) {
            RubyMethodScope methodScope;
            if (_inBlock) {
                var blockScope = scope as RubyBlockScope;
                if (blockScope == null || RubyOps.RefineBlocksRunning != 0) {
                    return false;
                }
                methodScope = blockScope.Parent as RubyMethodScope;
            } else {
                methodScope = scope as RubyMethodScope;
            }
            return methodScope != null && ReferenceEquals(methodScope.DefinitionRefinementsValue, _refinements);
        }
    }

    public abstract class AttributeDispatcher : MemberDispatcher {
        internal string/*!*/ Name;

        // the dispatcher is per call site, so this is a per-site inline cache of the variable's slot
        internal InstanceVariableSite/*!*/ Site;

        internal static AttributeDispatcher CreateRubyObjectWriterDispatcher(Type/*!*/ delegateType, string/*!*/ name, int version) {
            var dispatcher = (AttributeDispatcher)CreateDispatcher(delegateType, 1, true, false, version, null, RubyObjectAttributeWriterDispatchersWithScope);
            if (dispatcher != null) {
                dispatcher.Initialize(name, version);
            }
            return dispatcher;
        }

        internal abstract void Initialize(string/*!*/ name, int version);

        /// <summary>
        /// The receiver as an IRubyObject and its class, or null: an exact RubyObject is told apart
        /// inline, without the interface cast (see MemberDispatcher.IsCurrentFor).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IRubyObject GetRubyObject(object self, out RubyClass cls) {
            if (self != null && self.GetType() == typeof(RubyObject)) {
                var obj = Unsafe.As<RubyObject>(self);
                cls = obj.ImmediateClass;
                return obj;
            }
            if (self != null && (self.GetType() == typeof(RubyClass) || self.GetType() == typeof(RubyModule))) {
                var module = Unsafe.As<RubyModule>(self);
                cls = module.ImmediateClass;
                return module;
            }
            IRubyObject result = self as IRubyObject;
            cls = (result != null) ? result.ImmediateClass : null;
            return result;
        }
    }

    public sealed class RubyObjectAttributeReaderDispatcherWithScope : AttributeDispatcher {
        internal override void Initialize(string/*!*/ name, int version) {
            Name = name;
            Site = new InstanceVariableSite(name);
            Version = version;
        }

        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, object>(Invoke<object>) :
                (object)new Func<CallSite, RubyScope, object, object>(Invoke<RubyScope>);
        }
        
        public object Invoke<TScope>(CallSite/*!*/ callSite, TScope/*!*/ scope, object self) {
            RubyClass cls;
            IRubyObject obj = GetRubyObject(self, out cls);
            if (obj != null && cls.Version.Method == Version) {
                return RubyOps.ReadInstanceVariable(cls.Context, obj, Site);
            } else {
                return ((CallSite<Func<CallSite, TScope, object, object>>)callSite).Update(callSite, scope, self);
            }
        }
    }

    public sealed class RubyObjectAttributeWriterDispatcherWithScope<T0> : AttributeDispatcher {
        internal override void Initialize(string/*!*/ name, int version) {
            Name = name;
            Site = new InstanceVariableSite(name);
            Version = version;
        }

        public override object/*!*/ CreateDelegate(bool isUntyped) {
            return isUntyped ?
                (object)new Func<CallSite, object, object, T0, object>(Invoke<object>) :
                (object)new Func<CallSite, RubyScope, object, T0, object>(Invoke<RubyScope>);
        }

        public object Invoke<TScope>(CallSite/*!*/ callSite, TScope/*!*/ scope, object self, T0 arg0) {
            RubyClass cls;
            IRubyObject obj = GetRubyObject(self, out cls);
            if (obj != null && cls.Version.Method == Version) {
                return RubyOps.WriteInstanceVariable(cls.Context, obj, (object)arg0, Site);
            } else {
                return ((CallSite<Func<CallSite, TScope, object, T0, object>>)callSite).Update(callSite, scope, self, arg0);
            }
        }
    }
}

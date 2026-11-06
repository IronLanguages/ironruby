/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System.Linq.Expressions;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Dynamic;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Runtime.Calls {
    /// <summary>
    /// Caches binders by what they bind, so that call sites that do the same thing share one binder and its rules.
    /// A lookup takes no lock; when two threads race for a missing binder, both may build one but only the first
    /// stored is ever returned.
    /// </summary>
    internal sealed class RubyMetaBinderFactory {
        private static RubyMetaBinderFactory _Shared;

        /// <summary>
        /// Sites shared across runtimes.
        /// </summary>
        internal static RubyMetaBinderFactory Shared {
            get {
                if (_Shared == null) {
                    Interlocked.CompareExchange(ref _Shared, new RubyMetaBinderFactory(null), null);
                }
                return _Shared;
            }
        }

        private readonly RubyContext _context;

        private readonly ConcurrentDictionary<(string/*!*/ Name, RubyCallSignature Signature), RubyCallAction>/*!*/ _callActions = new();
        private readonly ConcurrentDictionary<(int ScopeId, RubyCallSignature Signature), SuperCallAction>/*!*/ _superCallActions = new();

        // (typeof(action)) => binder:
        private readonly ConcurrentDictionary<Type, RubyConversionAction>/*!*/ _conversionActions = new();

        // (CompositeConversion) => binder; an array, read without a lock, as CompositeConversionAction.Make asks on every call:
        private readonly CompositeConversionAction[]/*!*/ _compositeConversionActions = new CompositeConversionAction[Enum.GetValues<CompositeConversion>().Length];

        // (type) => binder:
        private readonly ConcurrentDictionary<Type, GenericConversionAction>/*!*/ _genericConversionActions = new();

        // interop binders, created on first use:
        private ConcurrentDictionary<CallInfo, InteropBinder.CreateInstance> _interopCreateInstance;
        private ConcurrentDictionary<CallInfo, InteropBinder.Return> _interopReturn;
        private ConcurrentDictionary<CallInfo, InteropBinder.Invoke> _interopInvoke;
        private ConcurrentDictionary<(string, CallInfo), InteropBinder.InvokeMember> _interopInvokeMember;
        private ConcurrentDictionary<string, InteropBinder.GetMember> _interopGetMember;
        private ConcurrentDictionary<string, InteropBinder.TryGetMemberExact> _interopTryGetMemberExact;
        private ConcurrentDictionary<string, InteropBinder.SetMember> _interopSetMember;
        private ConcurrentDictionary<string, InteropBinder.SetMemberExact> _interopSetMemberExact;
        private ConcurrentDictionary<CallInfo, InteropBinder.GetIndex> _interopGetIndex;
        private ConcurrentDictionary<CallInfo, InteropBinder.SetIndex> _interopSetIndex;
        private ConcurrentDictionary<(string, CallInfo), InteropBinder.SetIndexedProperty> _interopSetIndexedProperty;
        private ConcurrentDictionary<ExpressionType, DynamicMetaObjectBinder> _interopOperation;
        private ConcurrentDictionary<(Type, bool), InteropBinder.Convert> _interopConvert;
        private InteropBinder.Splat _interopSplat;

        internal RubyMetaBinderFactory(RubyContext context) {
            _context = context;
        }

        private TBinder/*!*/ GetOrCreate<TKey, TBinder>(ref ConcurrentDictionary<TKey, TBinder> cache, TKey key, Func<TKey, RubyContext, TBinder>/*!*/ factory) {
            var binders = Volatile.Read(ref cache) ?? Interlocked.CompareExchange(ref cache, new(), null) ?? cache;
            return binders.GetOrAdd(key, factory, _context);
        }

        public RubyCallAction/*!*/ Call(string/*!*/ methodName, RubyCallSignature signature) {
            return _callActions.GetOrAdd((methodName, signature), static (key, context) => new RubyCallAction(context, key.Name, key.Signature), _context);
        }

        public SuperCallAction/*!*/ SuperCall(int/*!*/ lexicalScopeId, RubyCallSignature signature) {
            return _superCallActions.GetOrAdd((lexicalScopeId, signature), static (key, context) => new SuperCallAction(context, key.Signature, key.ScopeId), _context);
        }

        public TAction/*!*/ Conversion<TAction>() where TAction : RubyConversionAction, new() {
            return (TAction)_conversionActions.GetOrAdd(typeof(TAction), static (_, context) => new TAction() { Context = context }, _context);
        }

        public CompositeConversionAction/*!*/ CompositeConversion(CompositeConversion conversion) {
            CompositeConversionAction result = Volatile.Read(ref _compositeConversionActions[(int)conversion]);
            if (result != null) {
                return result;
            }

            lock (_compositeConversionActions) {
                result = _compositeConversionActions[(int)conversion];
                if (result == null) {
                    Volatile.Write(ref _compositeConversionActions[(int)conversion], result = CompositeConversionAction.Create(_context, conversion));
                }
                return result;
            }
        }

        public GenericConversionAction/*!*/ GenericConversionAction(Type/*!*/ type) {
            return _genericConversionActions.GetOrAdd(type, static (type, context) => new GenericConversionAction(context, type), _context);
        }

        public InteropBinder.CreateInstance/*!*/ InteropCreateInstance(CallInfo/*!*/ callInfo) {
            return GetOrCreate(ref _interopCreateInstance, callInfo, static (callInfo, context) => new InteropBinder.CreateInstance(context, callInfo));
        }

        public InteropBinder.Return/*!*/ InteropReturn(CallInfo/*!*/ callInfo) {
            return GetOrCreate(ref _interopReturn, callInfo, static (callInfo, context) => new InteropBinder.Return(context, callInfo));
        }

        public InteropBinder.Invoke/*!*/ InteropInvoke(CallInfo/*!*/ callInfo) {
            return GetOrCreate(ref _interopInvoke, callInfo, static (callInfo, context) => new InteropBinder.Invoke(context, callInfo));
        }

        public InteropBinder.InvokeMember/*!*/ InteropInvokeMember(string/*!*/ name, CallInfo/*!*/ callInfo) {
            return GetOrCreate(ref _interopInvokeMember, (name, callInfo), static (key, context) => new InteropBinder.InvokeMember(context, key.Item1, key.Item2, null));
        }

        public InteropBinder.GetMember/*!*/ InteropGetMember(string/*!*/ name) {
            return GetOrCreate(ref _interopGetMember, name, static (name, context) => new InteropBinder.GetMember(context, name, null));
        }

        public InteropBinder.TryGetMemberExact/*!*/ InteropTryGetMemberExact(string/*!*/ name) {
            return GetOrCreate(ref _interopTryGetMemberExact, name, static (name, context) => new InteropBinder.TryGetMemberExact(context, name));
        }

        public InteropBinder.SetMember/*!*/ InteropSetMember(string/*!*/ name) {
            return GetOrCreate(ref _interopSetMember, name, static (name, context) => new InteropBinder.SetMember(context, name));
        }

        public InteropBinder.SetMemberExact/*!*/ InteropSetMemberExact(string/*!*/ name) {
            return GetOrCreate(ref _interopSetMemberExact, name, static (name, context) => new InteropBinder.SetMemberExact(context, name));
        }

        public InteropBinder.GetIndex/*!*/ InteropGetIndex(CallInfo/*!*/ callInfo) {
            return GetOrCreate(ref _interopGetIndex, callInfo, static (callInfo, context) => new InteropBinder.GetIndex(context, callInfo));
        }

        public InteropBinder.SetIndex/*!*/ InteropSetIndex(CallInfo/*!*/ callInfo) {
            return GetOrCreate(ref _interopSetIndex, callInfo, static (callInfo, context) => new InteropBinder.SetIndex(context, callInfo));
        }

        public InteropBinder.SetIndexedProperty/*!*/ InteropSetIndexedProperty(string/*!*/ name, CallInfo/*!*/ callInfo) {
            return GetOrCreate(ref _interopSetIndexedProperty, (name, callInfo), static (key, context) => new InteropBinder.SetIndexedProperty(context, key.Item1, key.Item2));
        }

        public InteropBinder.BinaryOperation/*!*/ InteropBinaryOperation(ExpressionType op) {
            return (InteropBinder.BinaryOperation)GetOrCreate(ref _interopOperation, op, static (op, context) => new InteropBinder.BinaryOperation(context, op));
        }

        public InteropBinder.UnaryOperation/*!*/ InteropUnaryOperation(ExpressionType op) {
            return (InteropBinder.UnaryOperation)GetOrCreate(ref _interopOperation, op, static (op, context) => new InteropBinder.UnaryOperation(context, op));
        }

        public InteropBinder.Convert/*!*/ InteropConvert(Type/*!*/ type, bool isExplicit) {
            return GetOrCreate(ref _interopConvert, (type, isExplicit), static (key, context) => new InteropBinder.Convert(context, key.Item1, key.Item2));
        }

        public InteropBinder.Splat/*!*/ InteropSplat() {
            return _interopSplat ??= new InteropBinder.Splat(_context);
        }
    }
}

/* ****************************************************************************
 *
 * Fiddle::Closure - a Ruby method C can call.
 *
 * The mirror image of Fiddle::Function.  A Function needs one IL instruction
 * whose signature is chosen at run time; a Closure needs a whole *method* whose
 * signature is chosen at run time, plus a delegate type to hand to
 * Marshal.GetFunctionPointerForDelegate.  Neither can be written in C# ahead of
 * time, so both are emitted:
 *
 *   - a delegate type, with [UnmanagedFunctionPointer(cc)], in a collectible
 *     dynamic assembly.  Its Invoke has the native signature.
 *   - a DynamicMethod with that signature plus a leading Closure argument,
 *     which boxes its arguments into an object[], calls Dispatch, and converts
 *     the answer back to the native return type.  CreateDelegate binds the
 *     Closure to the leading argument, so one emitted method serves every
 *     Closure with the same signature.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Fiddle {

    public static partial class FiddleOps {

        [RubyClass("Closure")]
        public class Closure : RubyObject {
            private int[]/*!*/ _argTypes = new int[0];
            private int _returnType;
            private IntPtr _address;
            private bool _freed;

            /// <summary>
            /// The emitted trampoline, bound to this Closure.  It has to be kept alive for
            /// as long as C might call it: the native side holds nothing but its address,
            /// and a collected delegate is a crash rather than an exception.
            /// </summary>
            private Delegate _trampoline;

            /// <summary>
            /// <c>method(:call).to_proc</c>, taken once at construction.  It dispatches to
            /// whatever #call the subclass defines, so Closure::BlockCaller's @block is
            /// still read at call time.
            /// </summary>
            private Proc _callback;

            private RubyContext _context;

            public Closure(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Closure(ImmediateClass.NominalClass);
            }

            internal IntPtr Address {
                get { return _address; }
            }

            #region Construction

            [RubyConstructor]
            public static Closure/*!*/ Create(BinaryOpStorage/*!*/ methodStorage, UnaryOpStorage/*!*/ toProcStorage,
                ConversionStorage<int>/*!*/ fixnumCast, RubyClass/*!*/ self, object returnType, object argTypes,
                [DefaultParameterValue(null)]object abi) {

                var result = new Closure(self);
                Reinitialize(methodStorage, toProcStorage, fixnumCast, result, returnType, argTypes, abi);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static Closure/*!*/ Reinitialize(BinaryOpStorage/*!*/ methodStorage, UnaryOpStorage/*!*/ toProcStorage,
                ConversionStorage<int>/*!*/ fixnumCast, Closure/*!*/ self, object returnType, object argTypes,
                [DefaultParameterValue(null)]object abi) {

                RubyContext context = methodStorage.Context;
                int returnCode = Function.ToTypeCode(fixnumCast, returnType);
                self._context = context;
                self._returnType = returnCode;
                self._argTypes = Function.ReadTypes(fixnumCast, argTypes);
                self._freed = false;

                NativeCall.ClrType(returnCode);
                for (int i = 0; i < self._argTypes.Length; i++) {
                    if (NativeCall.ClrType(self._argTypes[i]) == typeof(void)) {
                        throw RubyExceptions.CreateTypeError("void is not a valid argument type");
                    }
                }

                // the gem's fiddle/closure.rb exposes these as #ctype and #args
                context.SetInstanceVariable(self, "@ctype", ScriptingRuntimeHelpers.Int32ToObject(returnCode));
                context.SetInstanceVariable(self, "@args", argTypes);

                var abiCode = (abi == null) ? Function.DEFAULT : Function.ToTypeCode(fixnumCast, abi);
                CallingConvention cc = (abiCode == Function.STDCALL) ? CallingConvention.StdCall : CallingConvention.Cdecl;

                // method(:call).to_proc, so that the subclass's own #call is what C reaches
                var methodSite = methodStorage.GetCallSite("method", 1);
                object callMethod = methodSite.Target(methodSite, self, context.CreateAsciiSymbol("call"));
                var toProcSite = toProcStorage.GetCallSite("to_proc", 0);
                self._callback = toProcSite.Target(toProcSite, callMethod) as Proc;
                if (self._callback == null) {
                    throw new DLError("Fiddle::Closure subclasses must define #call");
                }

                self._trampoline = Trampolines.Bind(self, self._argTypes, returnCode, cc);
                self._address = Marshal.GetFunctionPointerForDelegate(self._trampoline);
                return self;
            }

            #endregion

            #region Lifetime

            [RubyMethod("to_i")]
            [RubyMethod("to_int")]
            public static object ToInteger(Closure/*!*/ self) {
                return Protocols.Normalize((long)self._address);
            }

            /// <summary>
            /// Drops the trampoline.  Nothing unmaps it - the CLR owns the stub - but the
            /// delegate and the Proc behind it become collectible, and #freed? answers true,
            /// which is the part callers test.
            /// </summary>
            [RubyMethod("free")]
            public static object FreeClosure(Closure/*!*/ self) {
                self._freed = true;
                self._trampoline = null;
                self._callback = null;
                self._address = IntPtr.Zero;
                return null;
            }

            [RubyMethod("freed?")]
            public static bool IsFreed(Closure/*!*/ self) {
                return self._freed;
            }

            #endregion

            #region Dispatch

            /// <summary>
            /// An exception raised by Ruby inside a callback.  It cannot be thrown through
            /// the native frames between here and the Ruby that started the call, so it is
            /// parked and rethrown by Fiddle::Function#call once the native call returns.
            /// </summary>
            [ThreadStatic]
            private static Exception _pendingCallbackError;

            internal static void RethrowPendingCallbackError() {
                Exception error = _pendingCallbackError;
                if (error != null) {
                    _pendingCallbackError = null;
                    throw error;
                }
            }

            /// <summary>
            /// Called from the emitted trampoline.  Returns the native return value boxed
            /// the way the trampoline expects it: a Double for the floating types, an Int64
            /// for everything else, null for void.
            /// </summary>
            internal static object Dispatch(object closure, object[]/*!*/ args) {
                var self = (Closure)closure;
                try {
                    Proc callback = self._callback;
                    if (callback == null) {
                        return NativeCall.IsFloating(self._returnType) ? (object)0.0 : (object)0L;
                    }

                    var rubyArgs = new object[args.Length];
                    for (int i = 0; i < args.Length; i++) {
                        rubyArgs[i] = NativeCall.ToRubyResult(self._context, self._argTypes[i], args[i]);
                    }

                    object result = callback.Call(null, rubyArgs);
                    if (NativeCall.IsFloating(self._returnType)) {
                        return NativeCall.ToDouble(result);
                    }
                    if (self._returnType == FiddleType.Void) {
                        return 0L;
                    }
                    if (self._returnType == FiddleType.VoidP || self._returnType == FiddleType.ConstString) {
                        return (long)ToAddress(result);
                    }
                    return NativeCall.ToLong(result);
                } catch (Exception e) {
                    // thrown again from Fiddle::Function#call, which starts its CLR trace over:
                    RubyExceptionData.GetInstance(e).CaptureExceptionTrace(self._context);
                    _pendingCallbackError = e;
                    return NativeCall.IsFloating(self._returnType) ? (object)0.0 : (object)0L;
                }
            }

            #endregion
        }

        /// <summary>
        /// The emitter behind Fiddle::Closure: one delegate type and one trampoline method
        /// per native signature, shared by every Closure that has it.
        /// </summary>
        internal static class Trampolines {
            private static ModuleBuilder _module;
            private static int _counter;
            private static readonly Dictionary<string, KeyValuePair<Type, DynamicMethod>>/*!*/ _cache =
                new Dictionary<string, KeyValuePair<Type, DynamicMethod>>();

            private static readonly MethodInfo/*!*/ _dispatch =
                typeof(Closure).GetMethod("Dispatch", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            internal static Delegate/*!*/ Bind(Closure/*!*/ closure, int[]/*!*/ argTypes, int returnType, CallingConvention cc) {
                string key = NativeCall.SignatureKey(argTypes, returnType, cc);
                lock (_cache) {
                    KeyValuePair<Type, DynamicMethod> entry;
                    if (!_cache.TryGetValue(key, out entry)) {
                        entry = Emit(argTypes, returnType, cc);
                        _cache[key] = entry;
                    }
                    return entry.Value.CreateDelegate(entry.Key, closure);
                }
            }

            private static ModuleBuilder/*!*/ Module {
                get {
                    if (_module == null) {
                        var assembly = AssemblyBuilder.DefineDynamicAssembly(
                            new AssemblyName("IronRuby.Fiddle.Closures"), AssemblyBuilderAccess.RunAndCollect);
                        _module = assembly.DefineDynamicModule("Closures");
                    }
                    return _module;
                }
            }

            private static KeyValuePair<Type, DynamicMethod> Emit(int[]/*!*/ argTypes, int returnType, CallingConvention cc) {
                Type[] native = new Type[argTypes.Length];
                for (int i = 0; i < argTypes.Length; i++) {
                    native[i] = NativeCall.ClrType(argTypes[i]);
                }
                Type nativeReturn = NativeCall.ClrType(returnType);

                Type delegateType = MakeDelegateType(nativeReturn, native, cc);

                Type[] trampolineArgs = new Type[native.Length + 1];
                trampolineArgs[0] = typeof(object);
                Array.Copy(native, 0, trampolineArgs, 1, native.Length);

                var method = new DynamicMethod("fiddle_closure", nativeReturn, trampolineArgs,
                    typeof(Trampolines).Module, true);
                ILGenerator il = method.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, native.Length);
                il.Emit(OpCodes.Newarr, typeof(object));
                for (int i = 0; i < native.Length; i++) {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldarg, i + 1);
                    if (NativeCall.IsFloating(argTypes[i])) {
                        if (native[i] == typeof(float)) {
                            il.Emit(OpCodes.Conv_R8);
                        }
                        il.Emit(OpCodes.Box, typeof(double));
                    } else if (NativeCall.IsUnsigned(argTypes[i])) {
                        il.Emit(OpCodes.Conv_U8);
                        il.Emit(OpCodes.Box, typeof(ulong));
                    } else {
                        il.Emit(OpCodes.Conv_I8);
                        il.Emit(OpCodes.Box, typeof(long));
                    }
                    il.Emit(OpCodes.Stelem_Ref);
                }
                il.Emit(OpCodes.Call, _dispatch);

                if (nativeReturn == typeof(void)) {
                    il.Emit(OpCodes.Pop);
                } else if (nativeReturn == typeof(double)) {
                    il.Emit(OpCodes.Unbox_Any, typeof(double));
                } else if (nativeReturn == typeof(float)) {
                    il.Emit(OpCodes.Unbox_Any, typeof(double));
                    il.Emit(OpCodes.Conv_R4);
                } else {
                    il.Emit(OpCodes.Unbox_Any, typeof(long));
                    EmitNarrow(il, nativeReturn);
                }
                il.Emit(OpCodes.Ret);

                return new KeyValuePair<Type, DynamicMethod>(delegateType, method);
            }

            private static void EmitNarrow(ILGenerator/*!*/ il, Type/*!*/ target) {
                if (target == typeof(IntPtr)) {
                    il.Emit(OpCodes.Conv_I);
                } else if (target == typeof(int)) {
                    il.Emit(OpCodes.Conv_I4);
                } else if (target == typeof(uint)) {
                    il.Emit(OpCodes.Conv_U4);
                } else if (target == typeof(short)) {
                    il.Emit(OpCodes.Conv_I2);
                } else if (target == typeof(ushort)) {
                    il.Emit(OpCodes.Conv_U2);
                } else if (target == typeof(sbyte)) {
                    il.Emit(OpCodes.Conv_I1);
                } else if (target == typeof(byte)) {
                    il.Emit(OpCodes.Conv_U1);
                }
            }

            /// <summary>
            /// A delegate type with the given native signature.  Only the runtime can
            /// implement a delegate's Invoke, hence MethodImplAttributes.Runtime; the
            /// [UnmanagedFunctionPointer] is what makes GetFunctionPointerForDelegate hand
            /// out a stub C can call with the right convention.
            /// </summary>
            internal static Type/*!*/ MakeDelegateType(Type/*!*/ returnType, Type[]/*!*/ parameterTypes, CallingConvention cc) {
                return MakeDelegateType(returnType, parameterTypes, cc, false);
            }

            /// <summary>
            /// <paramref name="setLastError"/> makes the marshalling stub capture errno (or
            /// GetLastError) the instant the callee returns, for Marshal.GetLastPInvokeError
            /// to read; see the head of FiddleCall.cs.
            /// </summary>
            internal static Type/*!*/ MakeDelegateType(Type/*!*/ returnType, Type[]/*!*/ parameterTypes,
                CallingConvention cc, bool setLastError) {
                TypeBuilder type = Module.DefineType(
                    (setLastError ? "FiddleBound" : "FiddleClosure") + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.AnsiClass | TypeAttributes.AutoClass,
                    typeof(MulticastDelegate));

                ConstructorBuilder ctor = type.DefineConstructor(
                    MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public,
                    CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) });
                ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                MethodBuilder invoke = type.DefineMethod("Invoke",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                    returnType, parameterTypes);
                invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                if (setLastError) {
                    type.SetCustomAttribute(new CustomAttributeBuilder(
                        typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) }),
                        new object[] { cc },
                        new[] { typeof(UnmanagedFunctionPointerAttribute).GetField("SetLastError") },
                        new object[] { true }));
                } else {
                    type.SetCustomAttribute(new CustomAttributeBuilder(
                        typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) }),
                        new object[] { cc }));
                }

                return type.CreateTypeInfo();
            }
        }
    }
}

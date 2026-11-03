/* ****************************************************************************
 *
 * The machinery behind Fiddle::Function#call: turning a list of fiddle type
 * codes into a native signature, emitting the one IL instruction that can call
 * it, and moving Ruby values across the boundary in both directions.
 *
 * libffi exists because C has no way to build a call whose signature is only
 * known at run time.  IL does: OpCodes.Calli takes the signature as an operand.
 * So a Fiddle::Function is a DynamicMethod
 *
 *     object Thunk(IntPtr fn, object[] args)
 *
 * whose body unboxes each argument to the native type its fiddle type code
 * names, pushes them, pushes the function pointer, emits one calli with the
 * chosen unmanaged calling convention, and boxes whatever comes back.  The
 * thunk is cached per signature, so a Function called in a loop emits nothing
 * after the first call, and two Functions with the same signature share one.
 *
 * The one thing a calli cannot do is errno.  Since .NET 6 the runtime saves the
 * thread's system error before a managed-to-native transition and restores it
 * afterwards, so that a P/Invoke without SetLastError cannot disturb managed
 * code's last-error value.  A calli is such a transition, and errno is already
 * back to what it was by the time the next IL instruction runs - reading it
 * inside the thunk, one instruction after the call, answers 0 just the same,
 * and so does calling __errno_location() afterwards.
 *
 * The one transition that *does* keep errno is a delegate whose type carries
 * [UnmanagedFunctionPointer(..., SetLastError = true)]: the marshalling stub
 * reads the system error the instant the callee returns and parks it where
 * Marshal.GetLastPInvokeError() can find it.  So there is a second thunk
 * flavour here, emitted only for a Function asked to save the error
 * (Function.new(..., save_last_error: true), which is what FFI's
 * attach_function uses so that FFI.errno works).  It goes through
 * Marshal.GetDelegateForFunctionPointer instead of calli and is therefore a
 * little slower, which is why it is opt-in rather than the default.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Fiddle {

    public static partial class FiddleOps {

        /// <summary>The shape every emitted thunk has.</summary>
        internal delegate object NativeThunk(IntPtr function, object[]/*!*/ args);

        /// <summary>
        /// The shape of the errno-preserving thunk: the same work, but the call goes
        /// through a [UnmanagedFunctionPointer(SetLastError = true)] delegate instead of
        /// a calli, so that the marshalling stub captures errno for us.
        /// </summary>
        internal delegate object BoundThunk(Delegate/*!*/ function, object[]/*!*/ args);

        /// <summary>A signature's emitted delegate type and the thunk that invokes it.</summary>
        internal sealed class ErrnoThunk {
            internal Type/*!*/ DelegateType;
            internal BoundThunk/*!*/ Thunk;
        }

        internal static class NativeCall {

            #region Type mapping

            /// <summary>
            /// The CLR type a fiddle type code names.  A negative code is the unsigned
            /// flavour of its positive twin.  TYPE_LONG follows the C compiler, not the CLR:
            /// 4 bytes on Windows, 8 on LP64 Unix.
            /// </summary>
            internal static Type/*!*/ ClrType(int type) {
                switch (type) {
                    case FiddleType.Void: return typeof(void);
                    case FiddleType.VoidP: return typeof(IntPtr);
                    case FiddleType.ConstString: return typeof(IntPtr);
                    case FiddleType.Char: return typeof(sbyte);
                    case -FiddleType.Char: return typeof(byte);
                    case FiddleType.Short: return typeof(short);
                    case -FiddleType.Short: return typeof(ushort);
                    case FiddleType.Int: return typeof(int);
                    case -FiddleType.Int: return typeof(uint);
                    case FiddleType.Long: return (LongSize == 4) ? typeof(int) : typeof(long);
                    case -FiddleType.Long: return (LongSize == 4) ? typeof(uint) : typeof(ulong);
                    case FiddleType.LongLong: return typeof(long);
                    case -FiddleType.LongLong: return typeof(ulong);
                    case FiddleType.Float: return typeof(float);
                    case FiddleType.Double: return typeof(double);
                    case FiddleType.Bool: return typeof(byte);
                    default:
                        throw RubyExceptions.CreateTypeError(
                            "unknown fiddle type " + type.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            internal static bool IsFloating(int type) {
                return type == FiddleType.Float || type == FiddleType.Double;
            }

            internal static bool IsUnsigned(int type) {
                return type < 0 || type == FiddleType.VoidP || type == FiddleType.ConstString || type == FiddleType.Bool;
            }

            #endregion

            #region The emitter

            private static readonly Dictionary<string, NativeThunk>/*!*/ _thunks = new Dictionary<string, NativeThunk>();

            internal static string/*!*/ SignatureKey(int[]/*!*/ argTypes, int returnType, CallingConvention cc) {
                var key = new StringBuilder();
                key.Append((int)cc).Append(':').Append(returnType).Append('(');
                for (int i = 0; i < argTypes.Length; i++) {
                    key.Append(argTypes[i]).Append(',');
                }
                key.Append(')');
                return key.ToString();
            }

            internal static NativeThunk/*!*/ GetThunk(int[]/*!*/ argTypes, int returnType, CallingConvention cc) {
                string key = SignatureKey(argTypes, returnType, cc);
                lock (_thunks) {
                    NativeThunk cached;
                    if (_thunks.TryGetValue(key, out cached)) {
                        return cached;
                    }
                    NativeThunk thunk = Emit(argTypes, returnType, cc);
                    _thunks[key] = thunk;
                    return thunk;
                }
            }

            private static NativeThunk/*!*/ Emit(int[]/*!*/ argTypes, int returnType, CallingConvention cc) {
                Type[] native = new Type[argTypes.Length];
                for (int i = 0; i < argTypes.Length; i++) {
                    native[i] = ClrType(argTypes[i]);
                    if (native[i] == typeof(void)) {
                        throw RubyExceptions.CreateTypeError("void is not a valid argument type");
                    }
                }
                Type nativeReturn = ClrType(returnType);

                var method = new DynamicMethod("fiddle_calli", typeof(object),
                    new[] { typeof(IntPtr), typeof(object[]) }, typeof(NativeCall).Module, true);
                ILGenerator il = method.GetILGenerator();

                EmitArguments(il, argTypes, native);

                il.Emit(OpCodes.Ldarg_0);
                il.EmitCalli(OpCodes.Calli, cc, nativeReturn, native);
                EmitBoxResult(il, returnType, nativeReturn);
                il.Emit(OpCodes.Ret);

                return (NativeThunk)method.CreateDelegate(typeof(NativeThunk));
            }

            /// <summary>Unboxes args[0..n] and pushes them as the native parameter types.</summary>
            private static void EmitArguments(ILGenerator/*!*/ il, int[]/*!*/ argTypes, Type[]/*!*/ native) {
                for (int i = 0; i < argTypes.Length; i++) {
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    if (IsFloating(argTypes[i])) {
                        // every floating argument arrives boxed as a Double
                        il.Emit(OpCodes.Unbox_Any, typeof(double));
                        if (native[i] == typeof(float)) {
                            il.Emit(OpCodes.Conv_R4);
                        }
                    } else {
                        // everything else arrives boxed as an Int64
                        il.Emit(OpCodes.Unbox_Any, typeof(long));
                        EmitNarrow(il, native[i]);
                    }
                }
            }

            #endregion

            #region The errno-preserving emitter

            private static readonly Dictionary<string, ErrnoThunk>/*!*/ _errnoThunks = new Dictionary<string, ErrnoThunk>();

            /// <summary>
            /// The delegate type and thunk for a signature whose calls have to leave errno
            /// readable.  The delegate type is the one Fiddle::Closure's emitter builds,
            /// only with SetLastError on, which is what makes the runtime keep the system
            /// error instead of restoring it over the transition.
            /// </summary>
            internal static ErrnoThunk/*!*/ GetErrnoThunk(int[]/*!*/ argTypes, int returnType, CallingConvention cc) {
                string key = SignatureKey(argTypes, returnType, cc);
                lock (_errnoThunks) {
                    ErrnoThunk cached;
                    if (_errnoThunks.TryGetValue(key, out cached)) {
                        return cached;
                    }
                    ErrnoThunk thunk = EmitBound(argTypes, returnType, cc);
                    _errnoThunks[key] = thunk;
                    return thunk;
                }
            }

            private static ErrnoThunk/*!*/ EmitBound(int[]/*!*/ argTypes, int returnType, CallingConvention cc) {
                Type[] native = new Type[argTypes.Length];
                for (int i = 0; i < argTypes.Length; i++) {
                    native[i] = ClrType(argTypes[i]);
                    if (native[i] == typeof(void)) {
                        throw RubyExceptions.CreateTypeError("void is not a valid argument type");
                    }
                }
                Type nativeReturn = ClrType(returnType);
                Type delegateType = Trampolines.MakeDelegateType(nativeReturn, native, cc, true);

                var method = new DynamicMethod("fiddle_bound", typeof(object),
                    new[] { typeof(Delegate), typeof(object[]) }, typeof(NativeCall).Module, true);
                ILGenerator il = method.GetILGenerator();

                // the delegate goes on the stack before its arguments
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, delegateType);
                EmitArguments(il, argTypes, native);
                il.Emit(OpCodes.Callvirt, delegateType.GetMethod("Invoke"));
                EmitBoxResult(il, returnType, nativeReturn);
                il.Emit(OpCodes.Ret);

                return new ErrnoThunk {
                    DelegateType = delegateType,
                    Thunk = (BoundThunk)method.CreateDelegate(typeof(BoundThunk))
                };
            }

            /// <summary>Narrows the Int64 on the stack to the native parameter type.</summary>
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
                // long and ulong are already what is on the stack
            }

            /// <summary>
            /// Boxes the native return value as the one thing the caller then reads: null
            /// for void, a Double for the floating types, an Int64 for the signed integers
            /// and a UInt64 for everything unsigned, pointers included.
            /// </summary>
            private static void EmitBoxResult(ILGenerator/*!*/ il, int returnType, Type/*!*/ nativeReturn) {
                if (nativeReturn == typeof(void)) {
                    il.Emit(OpCodes.Ldnull);
                    return;
                }
                if (nativeReturn == typeof(float)) {
                    il.Emit(OpCodes.Conv_R8);
                    il.Emit(OpCodes.Box, typeof(double));
                    return;
                }
                if (nativeReturn == typeof(double)) {
                    il.Emit(OpCodes.Box, typeof(double));
                    return;
                }
                if (IsUnsigned(returnType)) {
                    il.Emit(OpCodes.Conv_U8);
                    il.Emit(OpCodes.Box, typeof(ulong));
                } else {
                    il.Emit(OpCodes.Conv_I8);
                    il.Emit(OpCodes.Box, typeof(long));
                }
            }

            #endregion

            #region Arguments

            /// <summary>
            /// A block allocated for the duration of one call, holding the bytes of a String
            /// argument.  CRuby hands C the String's own buffer; a MutableString has no
            /// stable address, so the bytes go out and - for a writable, non-const pointer -
            /// come back.
            /// </summary>
            internal struct Temporary {
                internal IntPtr Block;
                internal MutableString String;
                internal int Count;
                internal bool CopyBack;
                /// <summary>
                /// The block is a String's from pack("p") (PackedPointers), not the call's: it is
                /// copied back but not freed, and held here so that it outlives the call.
                /// </summary>
                internal PackedPointers.Block Packed;
            }

            internal static long ToLong(object value) {
                if (value is int) {
                    return (int)value;
                }
                if (value is BigInteger) {
                    return (long)(BigInteger)value;
                }
                if (value is long) {
                    return (long)value;
                }
                if (value == null) {
                    return 0;
                }
                if (value is bool) {
                    return ((bool)value) ? 1 : 0;
                }
                if (value is double) {
                    return (long)(double)value;
                }
                var ptr = value as Pointer;
                if (ptr != null) {
                    return (long)ptr.Address;
                }
                throw RubyExceptions.CreateTypeError("no implicit conversion into Integer");
            }

            internal static double ToDouble(object value) {
                if (value is double) {
                    return (double)value;
                }
                if (value is int) {
                    return (int)value;
                }
                if (value is BigInteger) {
                    return (double)(BigInteger)value;
                }
                if (value == null) {
                    return 0.0;
                }
                throw RubyExceptions.CreateTypeError("no implicit conversion into Float");
            }

            /// <summary>
            /// The address behind a value standing in for a pointer.  A String is copied into
            /// a temporary block that is freed - and, unless the parameter is a const string,
            /// copied back into the String - once the call returns.
            /// </summary>
            internal static long ToPointerArgument(RespondToStorage/*!*/ respondTo, UnaryOpStorage/*!*/ toPtr,
                UnaryOpStorage/*!*/ toStr, object value, bool isConst,
                List<Temporary>/*!*/ temporaries, List<Pointer>/*!*/ pointers) {

                if (value == null) {
                    return 0;
                }

                var str = value as MutableString;
                if (str != null) {
                    byte[] bytes = str.ToByteArray();
                    IntPtr block = Allocate(bytes.Length + 1);
                    if (bytes.Length > 0) {
                        Marshal.Copy(bytes, 0, block, bytes.Length);
                    }
                    Marshal.WriteByte(block, bytes.Length, 0);
                    temporaries.Add(new Temporary {
                        Block = block,
                        String = str,
                        Count = bytes.Length,
                        CopyBack = !isConst && !str.IsFrozen
                    });
                    return (long)block;
                }

                var ptr = value as Pointer;
                if (ptr != null) {
                    if (ptr.BackingString != null) {
                        pointers.Add(ptr);
                    }
                    return (long)ptr.Address;
                }

                if (value is int || value is BigInteger || value is long) {
                    long address = ToLong(value);
                    // An address pack("p") wrote for a String: C gets the String's bytes as they
                    // are now, and what it writes there goes back into the String, as through the
                    // String's own buffer in CRuby - reline's Win32API passes its buffers so.
                    PackedPointers.Block packed = PackedPointers.Find(address);
                    if (packed != null) {
                        temporaries.Add(new Temporary {
                            Block = packed.Address,
                            String = packed.String,
                            Count = packed.Refresh(),
                            CopyBack = !isConst && !packed.String.IsFrozen,
                            Packed = packed
                        });
                    }
                    return address;
                }

                var handle = value as Handle;
                if (handle != null) {
                    return (long)handle.Address;
                }
                var function = value as Function;
                if (function != null) {
                    return (long)function.Address;
                }
                var closure = value as Closure;
                if (closure != null) {
                    return (long)closure.Address;
                }

                if (Protocols.RespondTo(respondTo, value, "to_ptr")) {
                    var site = toPtr.GetCallSite("to_ptr", 0);
                    object converted = site.Target(site, value);
                    if (!ReferenceEquals(converted, value)) {
                        return ToPointerArgument(respondTo, toPtr, toStr, converted, isConst, temporaries, pointers);
                    }
                }

                if (Protocols.RespondTo(respondTo, value, "to_str")) {
                    var site = toStr.GetCallSite("to_str", 0);
                    object converted = site.Target(site, value);
                    if (converted is MutableString) {
                        return ToPointerArgument(respondTo, toPtr, toStr, converted, isConst, temporaries, pointers);
                    }
                }

                throw RubyExceptions.CreateTypeError("no implicit conversion into Fiddle::Pointer");
            }

            /// <summary>
            /// Copies each temporary block back into the String it came from and frees it -
            /// unless the call handed that very block back (strcpy answers its destination),
            /// in which case the returned Pointer takes the block over rather than being
            /// left dangling.
            /// </summary>
            internal static void ReleaseTemporaries(List<Temporary>/*!*/ temporaries, List<Pointer>/*!*/ pointers,
                Pointer result) {

                for (int i = 0; i < temporaries.Count; i++) {
                    Temporary t = temporaries[i];
                    if (t.Packed != null) {
                        // the String's own block: it stays, for the String and its address
                        if (t.CopyBack) {
                            t.Packed.CopyBack(t.Count);
                        }
                        continue;
                    }
                    if (t.CopyBack && t.String != null && !t.String.IsFrozen) {
                        for (int j = 0; j < t.Count; j++) {
                            t.String.SetByte(j, Marshal.ReadByte(t.Block, j));
                        }
                    }
                    if (result != null && result.IsInside(t.Block, t.Count + 1)) {
                        result.AdoptBlock(t.Block);
                    } else {
                        Release(t.Block);
                    }
                }
                for (int i = 0; i < pointers.Count; i++) {
                    pointers[i].SyncBackingString();
                }
            }

            #endregion

            #region Results

            /// <summary>
            /// Turns what the thunk boxed into the Ruby object the return type names: a
            /// Pointer for TYPE_VOIDP, a String for TYPE_CONST_STRING, true/false for
            /// TYPE_BOOL, nil for void, an Integer or Float for the rest.
            /// </summary>
            internal static object ToRubyResult(RubyContext/*!*/ context, int returnType, object raw) {
                switch (returnType) {
                    case FiddleType.Void:
                        return null;

                    case FiddleType.VoidP:
                        return Pointer.Create(context, (IntPtr)(long)(ulong)raw, 0, IntPtr.Zero);

                    case FiddleType.ConstString: {
                        var address = (IntPtr)(long)(ulong)raw;
                        if (address == IntPtr.Zero) {
                            return null;
                        }
                        int length = 0;
                        while (Marshal.ReadByte(address, length) != 0) {
                            length++;
                        }
                        return Pointer.ReadBytes(address, length);
                    }

                    case FiddleType.Bool:
                        return ScriptingRuntimeHelpers.BooleanToObject((ulong)raw != 0);

                    case FiddleType.Float:
                    case FiddleType.Double:
                        return raw;

                    default:
                        if (raw is ulong) {
                            return Protocols.Normalize((ulong)raw);
                        }
                        return Protocols.Normalize((long)raw);
                }
            }

            #endregion

        }
    }
}

/* ****************************************************************************
 *
 * Fiddle::Function - a foreign function with a signature chosen at run time.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Fiddle {

    public static partial class FiddleOps {

        /// <summary>
        /// Function.new(address, arg_types, return_type) and #call.  The call goes through
        /// an emitted calli thunk (see FiddleCall.cs), cached per signature.
        ///
        /// #abi, #ptr, #name, #need_gvl? and #to_proc are the fiddle gem's own Ruby, in
        /// Src/StdLib/ironruby/fiddle/function.rb, reading the instance variables set here -
        /// exactly as they read the ones fiddle.so sets under CRuby.
        /// </summary>
        [RubyClass("Function")]
        public class Function : RubyObject {
            private IntPtr _address;
            private int[]/*!*/ _argTypes = new int[0];
            private int _returnType;
            private CallingConvention _callingConvention = CallingConvention.Cdecl;
            private NativeThunk _thunk;

            /// <summary>
            /// Set by the <c>save_last_error: true</c> option.  The call then goes through
            /// the errno-preserving thunk in FiddleCall.cs and fills Fiddle.last_error in,
            /// the way CRuby's fiddle does for every call.  It is opt-in because that path
            /// is a marshalled delegate rather than a calli.
            /// </summary>
            private bool _saveLastError;
            private ErrnoThunk _errnoThunk;
            private Delegate _bound;

            /// <summary>True when the type list ends with TYPE_VARIADIC.</summary>
            private bool _variadic;

            /// <summary>
            /// Keeps a Closure passed as the function itself alive: the native side holds
            /// nothing but the trampoline's address.
            /// </summary>
            private object _target;

            /// <summary>FFI_DEFAULT_ABI, as fiddle reports it on this platform.</summary>
            [RubyConstant]
            public readonly static int DEFAULT = IsWindows ? 1 : 2;

            /// <summary>FFI_STDCALL.  Only 32-bit Windows has a separate stdcall ABI.</summary>
            [RubyConstant]
            public readonly static int STDCALL = 5;

            public Function(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Function(ImmediateClass.NominalClass);
            }

            internal IntPtr Address {
                get { return _address; }
            }

            #region Construction

            /// <summary>
            /// The names fiddle 1.1 accepts in place of the TYPE_* codes - :int, "size_t",
            /// :const_string and the rest, which are Fiddle::Types' constants lowercased.
            /// </summary>
            private static readonly Dictionary<string, int>/*!*/ _typeNames = BuildTypeNames();

            private static Dictionary<string, int>/*!*/ BuildTypeNames() {
                var m = new Dictionary<string, int>();
                m["void"] = TYPE_VOID;
                m["voidp"] = TYPE_VOIDP;
                m["char"] = TYPE_CHAR;
                m["uchar"] = TYPE_UCHAR;
                m["short"] = TYPE_SHORT;
                m["ushort"] = TYPE_USHORT;
                m["int"] = TYPE_INT;
                m["uint"] = TYPE_UINT;
                m["long"] = TYPE_LONG;
                m["ulong"] = TYPE_ULONG;
                m["long_long"] = TYPE_LONG_LONG;
                m["ulong_long"] = TYPE_ULONG_LONG;
                m["float"] = TYPE_FLOAT;
                m["double"] = TYPE_DOUBLE;
                m["variadic"] = TYPE_VARIADIC;
                m["const_string"] = TYPE_CONST_STRING;
                m["bool"] = TYPE_BOOL;
                m["size_t"] = TYPE_SIZE_T;
                m["ssize_t"] = TYPE_SSIZE_T;
                m["ptrdiff_t"] = TYPE_PTRDIFF_T;
                m["intptr_t"] = TYPE_INTPTR_T;
                m["uintptr_t"] = TYPE_UINTPTR_T;
                m["int8_t"] = TYPE_INT8_T;
                m["uint8_t"] = TYPE_UINT8_T;
                m["int16_t"] = TYPE_INT16_T;
                m["uint16_t"] = TYPE_UINT16_T;
                m["int32_t"] = TYPE_INT32_T;
                m["uint32_t"] = TYPE_UINT32_T;
                m["int64_t"] = TYPE_INT64_T;
                m["uint64_t"] = TYPE_UINT64_T;
                return m;
            }

            /// <summary>
            /// A fiddle type: a TYPE_* code, one of the names above as a Symbol or String,
            /// or anything with #to_int - which is asked exactly once, as CRuby asks it.
            /// </summary>
            internal static int ToTypeCode(ConversionStorage<int>/*!*/ fixnumCast, object value) {
                if (value is int) {
                    return (int)value;
                }
                if (value is System.Numerics.BigInteger) {
                    return (int)(System.Numerics.BigInteger)value;
                }

                string name = null;
                var symbol = value as RubySymbol;
                if (symbol != null) {
                    name = symbol.ToString();
                } else {
                    var str = value as MutableString;
                    if (str != null) {
                        name = str.ToString();
                    }
                }
                if (name != null) {
                    int code;
                    if (_typeNames.TryGetValue(name, out code)) {
                        return code;
                    }
                    throw RubyExceptions.CreateTypeError("unknown type: " + name);
                }

                return Protocols.CastToFixnum(fixnumCast, value);
            }

            internal static int[]/*!*/ ReadTypes(ConversionStorage<int>/*!*/ fixnumCast, object types) {
                var list = types as IList;
                if (list == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion into Array");
                }
                var result = new int[list.Count];
                for (int i = 0; i < list.Count; i++) {
                    result[i] = ToTypeCode(fixnumCast, list[i]);
                    if (result[i] == FiddleType.Variadic && i != list.Count - 1) {
                        throw RubyExceptions.CreateArgumentError("TYPE_VARIADIC must be the last argument type");
                    }
                }
                // [TYPE_VOID] is C's f(void): a list of no arguments, not one void argument.
                if (result.Length == 1 && result[0] == FiddleType.Void) {
                    return new int[0];
                }
                return result;
            }

            private static void Reinitialize(RubyContext/*!*/ context, ConversionStorage<int>/*!*/ fixnumCast,
                Function/*!*/ self, object address, object argTypes, int returnType, int abi, object options) {

                self._target = address;
                self._address = ToAddress(address);
                self._argTypes = ReadTypes(fixnumCast, argTypes);
                self._returnType = returnType;
                self._callingConvention = (abi == STDCALL) ? CallingConvention.StdCall : CallingConvention.Cdecl;
                self._thunk = null;
                self._errnoThunk = null;
                self._bound = null;
                self._variadic = self._argTypes.Length > 0 && self._argTypes[self._argTypes.Length - 1] == FiddleType.Variadic;

                // validate the signature now rather than at the first call
                NativeCall.ClrType(returnType);
                for (int i = 0; i < self._argTypes.Length - (self._variadic ? 1 : 0); i++) {
                    if (NativeCall.ClrType(self._argTypes[i]) == typeof(void)) {
                        throw RubyExceptions.CreateTypeError("void is not a valid argument type");
                    }
                }

                object name = null, needGvl = null, saveLastError = null;
                var hash = options as IDictionary<object, object>;
                if (hash != null) {
                    hash.TryGetValue(context.CreateAsciiSymbol("name"), out name);
                    hash.TryGetValue(context.CreateAsciiSymbol("need_gvl"), out needGvl);
                    hash.TryGetValue(context.CreateAsciiSymbol("save_last_error"), out saveLastError);
                }
                self._saveLastError = Protocols.IsTrue(saveLastError);

                // the gem's fiddle/function.rb reads these
                context.SetInstanceVariable(self, "@ptr", address);
                context.SetInstanceVariable(self, "@abi", ScriptingRuntimeHelpers.Int32ToObject(abi));
                context.SetInstanceVariable(self, "@name", name);
                context.SetInstanceVariable(self, "@need_gvl", ScriptingRuntimeHelpers.BooleanToObject(Protocols.IsTrue(needGvl)));
            }

            [RubyConstructor]
            public static Function/*!*/ Create(ConversionStorage<int>/*!*/ fixnumCast, RubyClass/*!*/ self, object address,
                object argTypes, object returnType,
                [DefaultParameterValue(null)]object abi, [DefaultParameterValue(null)]object options) {

                var result = new Function(self);
                Reinitialize(fixnumCast.Context, fixnumCast, result, address, argTypes,
                    ToTypeCode(fixnumCast, returnType), ReadAbi(fixnumCast, abi, ref options), options);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static Function/*!*/ Reinitialize(ConversionStorage<int>/*!*/ fixnumCast, Function/*!*/ self, object address,
                object argTypes, object returnType,
                [DefaultParameterValue(null)]object abi, [DefaultParameterValue(null)]object options) {

                Reinitialize(fixnumCast.Context, fixnumCast, self, address, argTypes,
                    ToTypeCode(fixnumCast, returnType), ReadAbi(fixnumCast, abi, ref options), options);
                return self;
            }

            /// <summary>
            /// Function.new's fourth argument is the ABI, and its keywords arrive as a
            /// trailing Hash - which lands in <c>abi</c> when the ABI itself was left out.
            /// </summary>
            private static int ReadAbi(ConversionStorage<int>/*!*/ fixnumCast, object abi, ref object options) {
                if (abi == null) {
                    return DEFAULT;
                }
                if (abi is IDictionary<object, object> && options == null) {
                    options = abi;
                    return DEFAULT;
                }
                return ToTypeCode(fixnumCast, abi);
            }

            /// <summary>The free function a Pointer reports, as the callable Fiddle::Function it is.</summary>
            internal static Function/*!*/ CreateFreeFunction(RubyContext/*!*/ context, IntPtr address) {
                var result = new Function(context.GetClass(typeof(Function)));
                Reinitialize(context, new ConversionStorage<int>(context), result, Protocols.Normalize((long)address),
                    new RubyArray { ScriptingRuntimeHelpers.Int32ToObject(FiddleType.VoidP) },
                    FiddleType.Void, DEFAULT, null);
                return result;
            }

            #endregion

            #region Calling

            [RubyMethod("call")]
            public static object Call(RespondToStorage/*!*/ respondTo, UnaryOpStorage/*!*/ toPtr,
                UnaryOpStorage/*!*/ toStr, ConversionStorage<int>/*!*/ fixnumCast,
                Function/*!*/ self, params object[]/*!*/ args) {

                RubyContext context = respondTo.Context;
                if (self._address == IntPtr.Zero) {
                    throw new DLError("NULL function pointer");
                }

                int[] types;
                object[] values;
                if (self._variadic) {
                    SplitVariadic(fixnumCast, self, args, out types, out values);
                } else {
                    if (args.Length != self._argTypes.Length) {
                        throw RubyExceptions.CreateArgumentError(
                            String.Format("wrong number of arguments (given {0}, expected {1})", args.Length, self._argTypes.Length));
                    }
                    types = self._argTypes;
                    values = args;
                }

                var temporaries = new List<NativeCall.Temporary>();
                var pointers = new List<Pointer>();
                var converted = new object[values.Length];
                object rubyResult = null;

                try {
                    for (int i = 0; i < values.Length; i++) {
                        int type = types[i];
                        if (NativeCall.IsFloating(type)) {
                            converted[i] = NativeCall.ToDouble(values[i]);
                        } else if (type == FiddleType.VoidP || type == FiddleType.ConstString) {
                            converted[i] = NativeCall.ToPointerArgument(respondTo, toPtr, toStr, values[i],
                                type == FiddleType.ConstString, temporaries, pointers);
                        } else {
                            converted[i] = NativeCall.ToLong(values[i]);
                        }
                    }

                    object result;
                    if (self._saveLastError && !self.PrepareBoundCall(types)) {
                        self._saveLastError = false;
                    }
                    if (self._saveLastError) {
                        result = self._errnoThunk.Thunk(self._bound, converted);
                        // read it before anything else can disturb it
                        _lastError = ScriptingRuntimeHelpers.Int32ToObject(Marshal.GetLastPInvokeError());
                    } else {
                        NativeThunk thunk;
                        if (self._variadic) {
                            thunk = NativeCall.GetThunk(types, self._returnType, self._callingConvention);
                        } else {
                            thunk = self._thunk;
                            if (thunk == null) {
                                thunk = self._thunk = NativeCall.GetThunk(types, self._returnType, self._callingConvention);
                            }
                        }
                        result = thunk(self._address, converted);
                    }

                    // The result has to become a Ruby object here, inside the try: a
                    // TYPE_CONST_STRING return may point into a temporary block, and the
                    // finally below is what frees those.
                    rubyResult = NativeCall.ToRubyResult(context, self._returnType, result);
                } finally {
                    NativeCall.ReleaseTemporaries(temporaries, pointers, rubyResult as Pointer);
                }

                // Fiddle.last_error is only filled in for a Function made with
                // save_last_error: true; see the head of FiddleCall.cs for why a plain
                // calli leaves errno unreadable.
                Closure.RethrowPendingCallbackError();
                return rubyResult;
            }

            /// <summary>
            /// Binds the errno-preserving thunk to this function's address, and answers
            /// whether that path can be used at all.
            ///
            /// It cannot when the address is one of our own closures:
            /// GetDelegateForFunctionPointer recognises a pointer it handed out for a
            /// managed delegate and answers that very delegate, which has the closure's
            /// type rather than the SetLastError one the thunk casts to.  Calling a Ruby
            /// callback has no errno to report anyway, so such a Function falls back to
            /// the calli thunk for good.
            /// </summary>
            private bool PrepareBoundCall(int[]/*!*/ types) {
                // a variadic call's signature is per call, so nothing about it is cached
                if (_variadic || _errnoThunk == null || _bound == null) {
                    ErrnoThunk thunk = NativeCall.GetErrnoThunk(types, _returnType, _callingConvention);
                    Delegate bound = Marshal.GetDelegateForFunctionPointer(_address, thunk.DelegateType);
                    if (!thunk.DelegateType.IsInstanceOfType(bound)) {
                        _errnoThunk = null;
                        _bound = null;
                        return false;
                    }
                    _errnoThunk = thunk;
                    _bound = bound;
                }
                return true;
            }

            /// <summary>
            /// A variadic Function's own type list ends with TYPE_VARIADIC; the arguments
            /// that stand in for the "..." arrive as type/value pairs, so the signature the
            /// call is actually made with is built here, per call.
            ///
            /// A floating-point variadic argument is refused rather than passed.  The System
            /// V x86-64 ABI has the caller put the number of vector registers used in AL,
            /// and nothing in a calli emitted for a fixed signature promises to; an integer
            /// or pointer vararg does not care, a double silently might.
            /// </summary>
            private static void SplitVariadic(ConversionStorage<int>/*!*/ fixnumCast, Function/*!*/ self,
                object[]/*!*/ args, out int[] types, out object[] values) {

                int fixedCount = self._argTypes.Length - 1;
                if (args.Length < fixedCount || ((args.Length - fixedCount) % 2) != 0) {
                    throw RubyExceptions.CreateArgumentError(
                        String.Format("wrong number of arguments (given {0}, expected {1}+ and type/value pairs after them)",
                            args.Length, fixedCount));
                }

                int variadicCount = (args.Length - fixedCount) / 2;
                types = new int[fixedCount + variadicCount];
                values = new object[fixedCount + variadicCount];
                for (int i = 0; i < fixedCount; i++) {
                    types[i] = self._argTypes[i];
                    values[i] = args[i];
                }
                for (int i = 0; i < variadicCount; i++) {
                    int type = ToTypeCode(fixnumCast, args[fixedCount + i * 2]);
                    if (NativeCall.IsFloating(type)) {
                        throw RubyExceptions.CreateArgumentError(
                            "a floating-point variadic argument cannot be passed reliably on IronRuby");
                    }
                    types[fixedCount + i] = type;
                    values[fixedCount + i] = args[fixedCount + i * 2 + 1];
                }
            }

            #endregion
        }
    }
}

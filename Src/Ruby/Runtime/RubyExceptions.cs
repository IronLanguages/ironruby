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
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using IronRuby.Builtins;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Calls;
using System.Globalization;
using System.Numerics;
using System.Text;
using IronRuby.Compiler;

namespace IronRuby.Runtime {
    /// <summary>
    /// Helper class for creating the corresponding .NET exceptions from the Ruby error names
    /// </summary>
    public static class RubyExceptions {
        public static string FormatMessage(string/*!*/ message, params object[] args) {
            return args != null && args.Length > 0 ? String.Format(CultureInfo.InvariantCulture, message, args) : message;
        }

        #region TypeError (InvalidOperationException)

        public static Exception/*!*/ CreateTypeError(string/*!*/ message, params object[] args) {
            return CreateTypeError(null, message, args);
        }

        public static Exception/*!*/ CreateTypeError(Exception innerException, string/*!*/ message, params object[] args) {
            return new InvalidOperationException(FormatMessage(message, args), innerException);
        }

        /// <summary>
        /// MRI raises FrozenError (a RuntimeError subclass) with "can't modify frozen &lt;class&gt;: &lt;inspect&gt;".
        /// This overload is the fallback used where neither the class name nor a RubyContext is available.
        /// </summary>
        public static Exception/*!*/ CreateObjectFrozenError() {
            // MRI raises FrozenError here, not RuntimeError. FrozenError derives from
            // RuntimeError, so existing `rescue RuntimeError` handlers still catch it.
            return new FrozenError("can't modify frozen object");
        }

        /// <summary>
        /// rb_str_locktmp lends a string's bytes out - to IO::Buffer.for, for instance - and
        /// MRI reports a mutation attempt while it is lent as a plain RuntimeError.
        /// </summary>
        public static Exception/*!*/ CreateTemporarilyLockedError() {
            return new RuntimeError("can't modify string; temporarily locked");
        }

        /// <summary>
        /// MRI: "can't modify frozen String" - used where the class is statically known but no
        /// RubyContext is in hand to produce the inspect suffix.
        /// </summary>
        public static Exception/*!*/ CreateObjectFrozenError(string/*!*/ className) {
            return new FrozenError(String.Format(CultureInfo.InvariantCulture, "can't modify frozen {0}", className));
        }

        /// <summary>
        /// MRI: `can't modify frozen String: "abc"`, and under --debug-frozen-string-literal
        /// `can't modify frozen String: "abc", created at foo.rb:1` for a literal.
        ///
        /// The mutation guard sits in MutableString, below the assembly that has #inspect, so the
        /// quoting is done here. It covers the escapes MRI uses for ASCII and passes anything else
        /// through; an error message is the one place that is good enough, and the alternative was
        /// no receiver in the message at all.
        /// </summary>
        public static Exception/*!*/ CreateStringFrozenError(MutableString/*!*/ str) {
            var message = new StringBuilder("can't modify frozen String: ");
            AppendQuoted(message, str);

            string site = MutableString.GetLiteralSite(str);
            if (site != null) {
                message.Append(", created at ").Append(site);
            }

            return ((FrozenError)new FrozenError(message.ToString())).SetReceiver(str);
        }

        private static void AppendQuoted(StringBuilder/*!*/ result, MutableString/*!*/ str) {
            result.Append('"');
            foreach (char c in str.ConvertToString()) {
                switch (c) {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\t': result.Append("\\t"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\0': result.Append("\\0"); break;
                    case '\a': result.Append("\\a"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\v': result.Append("\\v"); break;
                    case (char)0x1b: result.Append("\\e"); break;
                    default:
                        if (c < 0x20 || c == 0x7f) {
                            result.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                        } else {
                            result.Append(c);
                        }
                        break;
                }
            }
            result.Append('"');
        }

        // Guards against #inspect itself mutating the frozen receiver, which would re-enter
        // CreateObjectFrozenError and blow the stack (an uncatchable failure on .NET).
        // MRI has the same problem and solves it the same way, printing "..." for the receiver.
        [ThreadStatic]
        private static int _frozenErrorInspectDepth;

        /// <summary>
        /// For the mutation guards that live on the object itself (Hash, Array, Struct, ...) and so
        /// have no RubyContext in hand: the process has one by the time any Ruby code can run.
        /// </summary>
        public static Exception/*!*/ CreateObjectFrozenErrorFor(object obj, string/*!*/ className) {
            var context = RubyContext._Default;
            return (context != null) ? CreateObjectFrozenError(context, obj) : CreateObjectFrozenError(className);
        }

        /// <summary>
        /// MRI: "can't modify frozen Array: [1, 2]".
        /// </summary>
        public static Exception/*!*/ CreateObjectFrozenError(RubyContext/*!*/ context, object obj) {
            string inspect;
            if (_frozenErrorInspectDepth > 0) {
                inspect = "...";
            } else {
                _frozenErrorInspectDepth++;
                try {
                    inspect = context.Inspect(obj).ToString();
                } catch (FrozenError nested) {
                    // #inspect mutated the frozen receiver; the nested error already spells the
                    // message with "..." in place of the receiver, which is what MRI reports too.
                    return nested;
                } catch (Exception) {
                    return CreateObjectFrozenError(context.GetClassDisplayName(obj));
                } finally {
                    _frozenErrorInspectDepth--;
                }
            }
            return ((FrozenError)new FrozenError(String.Format(CultureInfo.InvariantCulture, "can't modify frozen {0}: {1}",
                context.GetClassDisplayName(obj), inspect))).SetReceiver(obj);
        }

        /// <summary>
        /// The EXPLICIT conversion failure message: Integer(), Float(), Hash() and friends.
        /// MRI: Integer(nil) => "can't convert nil into Integer".
        /// For the implicit (to_str/to_int/to_ary/to_hash protocol) failure use
        /// <see cref="CreateImplicitConversionError"/> instead - MRI words those differently.
        /// </summary>
        public static Exception/*!*/ CreateTypeConversionError(string/*!*/ fromType, string/*!*/ toType) {
            Assert.NotNull(fromType, toType);
            return CreateTypeError("can't convert {0} into {1}", MessageTypeName(fromType), MessageTypeName(toType));
        }

        /// <summary>
        /// The IMPLICIT conversion failure message (MRI's rb_convert_type path):
        ///   [1] + 1        => "no implicit conversion of Integer into Array"
        ///   File.open(nil) => "no implicit conversion of nil into String"
        /// </summary>
        public static Exception/*!*/ CreateImplicitConversionError(string/*!*/ fromType, string/*!*/ toType) {
            Assert.NotNull(fromType, toType);
            string from = MessageTypeName(fromType), to = MessageTypeName(toType);
            // MRI before 4.1 said "from nil to integer" for this one (rb_num2long); 4.1 words it
            // like every other conversion failure, and ruby/spec holds other Rubies to that.
            return CreateTypeError("no implicit conversion of {0} into {1}", from, to);
        }

        /// <summary>
        /// How MRI spells a class in a TypeError message: nil, true and false are spelled by value.
        /// </summary>
        public static string/*!*/ MessageTypeName(string/*!*/ className) {
            switch (className) {
                case "NilClass": return "nil";
                case "TrueClass": return "true";
                case "FalseClass": return "false";
                default: return className;
            }
        }

        public static Exception/*!*/ CreateUnexpectedTypeError(RubyContext/*!*/ context, object param, string/*!*/ type) {
            return CreateTypeError("wrong argument type {0} (expected {1})", MessageTypeName(context.GetClassDisplayName(param)), MessageTypeName(type));
        }

        public static Exception/*!*/ CannotConvertTypeToTargetType(RubyContext/*!*/ context, object param, string/*!*/ toType) {
            Assert.NotNull(context, toType);
            return CreateTypeConversionError(context.GetClassName(param), toType);
        }

        public static Exception/*!*/ CreateAllocatorUndefinedError(RubyClass/*!*/ rubyClass) {
            return CreateTypeError("allocator undefined for {0}", rubyClass.Name);
        }

        public static Exception/*!*/ CreateNotClrTypeError(RubyClass/*!*/ rubyClass) {
            return CreateTypeError("'{0}' doesn't represent a CLR type", rubyClass.Name);
        }

        public static Exception/*!*/ CreateNotClrNamespaceError(RubyModule/*!*/ rubyModule) {
            return CreateTypeError("'{0}' doesn't represent a CLR namespace", rubyModule.Name);
        }

        public static Exception/*!*/ CreateMissingDefaultConstructorError(RubyClass/*!*/ rubyClass, string/*!*/ initializerOwnerName) {
            Debug.Assert(rubyClass.IsRubyClass);

            Type baseType = rubyClass.GetUnderlyingSystemType().BaseType;
            Debug.Assert(baseType != null);

            return CreateTypeError("can't allocate class '{1}' that derives from type '{0}' with no default constructor;" +
                " define {1}#new singleton method instead of {2}#initialize",
                rubyClass.Context.GetTypeName(baseType, true), rubyClass.Name, initializerOwnerName
            );
        }

        public static Exception/*!*/ MakeCoercionError(RubyContext/*!*/ context, object self, object other) {
            return CreateTypeError("{0} can't be coerced into {1}",
                CoercionOperandName(context, self), MessageTypeName(context.GetClassOf(other).Name));
        }

        /// <summary>
        /// What a failed coercion calls the operand. MRI's coerce_failed shows the value itself
        /// for the things whose inspect *is* their identity - nil, true, false, a Symbol, an
        /// Integer, a Float - and the class for everything else, so "3.0 can't be coerced into
        /// Integer" but "String can't be coerced into Integer".
        /// </summary>
        private static string/*!*/ CoercionOperandName(RubyContext/*!*/ context, object value) {
            if (value == null || value is bool || value is int || value is long || value is double ||
                value is BigInteger || value is RubySymbol) {
                return context.Inspect(value).ToString();
            }
            return MessageTypeName(context.GetClassOf(value).Name);
        }

        public static Exception/*!*/ CreateReturnTypeError(string/*!*/ className, string/*!*/ methodName, string/*!*/ returnTypeName) {
            return CreateTypeError("{0}#{1} should return {2}", className, methodName, returnTypeName);
        }

        /// <summary>
        /// An object promised a conversion and then broke the promise: it answers #to_ary but what
        /// came back is not an Array. MRI names both ends of the failed conversion and what was
        /// actually handed over:
        ///   "can't convert Foo into Array (Foo#to_ary gives NilClass)"
        /// The class of the returned value is spelled as the class, not by value, so a nil result
        /// reads NilClass - unlike the "no implicit conversion of nil into Array" message, which is
        /// about the argument rather than about what a conversion method gave back.
        /// </summary>
        public static Exception/*!*/ CreateReturnTypeError(string/*!*/ className, string/*!*/ methodName, string/*!*/ returnTypeName,
            object result) {

            string resultClass;
            if (result == null) {
                resultClass = "NilClass";
            } else {
                var context = RubyContext._Default;
                resultClass = (context != null) ? context.GetClassDisplayName(result) : result.GetType().Name;
            }

            return CreateTypeError("can't convert {0} into {1} ({0}#{2} gives {3})",
                MessageTypeName(className), returnTypeName, methodName, resultClass);
        }

        #endregion

        #region NameError (MemberAccessException)

        public static Exception/*!*/ CreateNameError(string/*!*/ message, params object[] args) {
            return new MemberAccessException(FormatMessage(message, args));
        }

        /// <summary>
        /// Records NameError#name and NameError#receiver on an already-created error.
        /// Returns the error so it can be used inline in a `throw`.
        /// </summary>
        public static Exception/*!*/ WithNameAndReceiver(Exception/*!*/ error, object name, object receiver) {
            var data = RubyExceptionData.GetInstance(error);
            data.Name = name;
            data.SetReceiver(receiver);
            return error;
        }

        /// <summary>
        /// Same, taking the name as a plain string and interning it as a Symbol, which is what
        /// MRI's NameError#name answers for a missing method, constant or class variable.
        /// </summary>
        public static Exception/*!*/ WithNameAndReceiver(RubyContext/*!*/ context, Exception/*!*/ error, string/*!*/ name, object receiver) {
            return WithNameAndReceiver(error, context.CreateSymbol(name, RubyEncoding.UTF8), receiver);
        }

        public static Exception/*!*/ CreateUndefinedMethodError(RubyModule/*!*/ module, string/*!*/ methodName) {
            return WithNameAndReceiver(module.Context,
                CreateNameError("undefined method '{0}' for {2} '{1}'",
                    methodName, GetModuleErrorName(module), module.IsClass ? "class" : "module"),
                methodName, module);
        }

        /// <summary>
        /// How MRI names a module in an error message. A metaclass is reported as the class or
        /// module it is attached to ("for class 'String'" rather than "#&lt;Class:String&gt;"),
        /// but the singleton class of an ordinary object keeps its own description. An anonymous
        /// module has no Name at all, so it falls back to #to_s - which is what the specs
        /// interpolate into their expected messages.
        /// </summary>
        public static string/*!*/ GetModuleErrorName(RubyModule/*!*/ module) {
            if (module.IsSingletonClass && ((RubyClass)module).SingletonClassOf is RubyModule attached) {
                module = attached;
            }
            return module.Name ?? module.GetDisplayName(module.Context, false).ToString();
        }

        #endregion

        #region ArgumentError (ArgumentException)

        public static Exception/*!*/ CreateArgumentError(string/*!*/ message, params object[] args) {
            return CreateArgumentError(null, message, args);
        }

        public static Exception/*!*/ CreateArgumentError(Exception innerException, string/*!*/ message, params object[] args) {
            return new ArgumentException(FormatMessage(message, args), innerException);
        }

        public static Exception/*!*/ InvalidValueForType(RubyContext/*!*/ context, object obj, string type) {
            return CreateArgumentError("invalid value for {0}: {1}", type, context.Inspect(obj));
        }

        public static Exception/*!*/ MakeComparisonError(RubyContext/*!*/ context, object self, object other) {
            // MRI's rb_cmperr: the left operand is always spelled by class, the right one by
            // *inspect* when it is an immediate (nil/true/false/Integer/Symbol/Float) and by class otherwise.
            //   1 < nil   => "comparison of Integer with nil failed"
            //   1 < "a"   => "comparison of Integer with String failed"
            //   [1,:b].max => "comparison of Integer with :b failed"
            // An anonymous class has no name; MRI prints it the way #inspect does (#<Class:0x...>).
            // the receiver is always named by class, so nil <=> 1 is "comparison of NilClass with 1 failed"
            string selfClass = context.GetClassOf(self).GetNonNullName(context);
            // An Integer is immediate in MRI only while it is a Fixnum (62 bits); a Bignum is
            // spelled by class like any other object.
            string otherClass;
            if (other == null || other is bool || other is int || other is double || other is RubySymbol
                || (other is long && IsFixnumRange((long)other))
                || (other is BigInteger && IsFixnumRange((BigInteger)other))) {
                try {
                    otherClass = context.Inspect(other).ToString();
                } catch (Exception) {
                    otherClass = MessageTypeName(context.GetClassOf(other).GetNonNullName(context));
                }
            } else {
                otherClass = MessageTypeName(context.GetClassOf(other).GetNonNullName(context));
            }
            return CreateArgumentError("comparison of {0} with {1} failed", selfClass, otherClass);
        }

        private static readonly BigInteger FixnumMax = BigInteger.Pow(2, 62) - 1;
        private static readonly BigInteger FixnumMin = -BigInteger.Pow(2, 62);

        private static bool IsFixnumRange(BigInteger value) {
            return value >= FixnumMin && value <= FixnumMax;
        }

        private static bool IsFixnumRange(long value) {
            return value >= -(1L << 62) && value <= (1L << 62) - 1;
        }

        #endregion

        #region Encoding Errors

        public static Exception/*!*/ CreateInvalidByteSequenceError(EncoderFallbackException/*!*/ e, RubyEncoding/*!*/ encoding) {
            return new InvalidByteSequenceError(
                FormatMessage(
                    "character U+{0:X4} can't be encoded in {1}",
                    e.CharUnknownHigh != '\0' ? Tokenizer.ToCodePoint(e.CharUnknownHigh, e.CharUnknownLow) : (int)e.CharUnknown, 
                    encoding
                )
            );
        }

        public static Exception/*!*/ CreateInvalidByteSequenceError(DecoderFallbackException/*!*/ e, RubyEncoding/*!*/ encoding) {
            // MRI spells the offending bytes the way String#inspect would - "\xFF" on UTF-8 -
            // and reports only the first one, however many followed it.
            var bytes = e.BytesUnknown;
            var text = new StringBuilder("\"");
            if (bytes != null && bytes.Length > 0) {
                text.Append("\\x").Append(bytes[0].ToString("X2"));
            }
            text.Append('"');
            return new InvalidByteSequenceError(FormatMessage("{0} on {1}", text.ToString(), encoding));
        }

        /// <summary>
        /// Spells a run of bytes the way String#inspect would, which is how MRI's transcoder names
        /// the bytes it rejected.
        /// </summary>
        public static string/*!*/ InspectTranscodingBytes(byte[]/*!*/ bytes) {
            var result = new StringBuilder(bytes.Length + 2);
            result.Append('"');
            foreach (byte b in bytes) {
                if (b >= 0x20 && b < 0x7f && b != (byte)'"' && b != (byte)'\\') {
                    result.Append((char)b);
                } else {
                    result.Append("\\x").Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            result.Append('"');
            return result.ToString();
        }

        public static string/*!*/ InvalidByteSequenceMessage(RubyEncoding/*!*/ source, byte[]/*!*/ errorBytes, byte[] readAgain, bool incomplete) {
            if (incomplete) {
                return FormatMessage("incomplete {0} on {1}", InspectTranscodingBytes(errorBytes), source.Name);
            }
            if (readAgain == null || readAgain.Length == 0) {
                return FormatMessage("{0} on {1}", InspectTranscodingBytes(errorBytes), source.Name);
            }
            return FormatMessage("{0} followed by {1} on {2}",
                InspectTranscodingBytes(errorBytes), InspectTranscodingBytes(readAgain), source.Name);
        }

        /// <summary>
        /// The error MRI raises when a transcoder meets bytes that are not a character of the source
        /// encoding. <paramref name="errorBytes"/> is the longest prefix that could still have grown
        /// into a character and <paramref name="readAgain"/> the byte that proved it could not.
        /// </summary>
        public static Exception/*!*/ CreateInvalidByteSequenceError(RubyEncoding/*!*/ source, RubyEncoding destination,
            byte[]/*!*/ errorBytes, byte[] readAgain, bool incomplete) {

            var error = new InvalidByteSequenceError(InvalidByteSequenceMessage(source, errorBytes, readAgain, incomplete));
            error.SourceEncoding = source;
            error.DestinationEncoding = destination;
            error.ErrorBytes = errorBytes;
            error.ReadAgainBytes = readAgain ?? new byte[0];
            error.IncompleteInput = incomplete;
            return error;
        }

        /// <summary>
        /// The encodings a conversion actually passes through. MRI's transcoder table has a direct
        /// converter between UTF-8 and nearly everything else and routes the rest through UTF-8, so
        /// a pair with UTF-8 at one end is one hop and any other pair is two.
        /// </summary>
        public static RubyEncoding/*!*/[]/*!*/ ConversionPath(RubyEncoding/*!*/ source, RubyEncoding/*!*/ destination) {
            if (source == RubyEncoding.UTF8 || destination == RubyEncoding.UTF8) {
                return new[] { source, destination };
            }
            return new[] { source, RubyEncoding.UTF8, destination };
        }

        /// <summary>
        /// The name MRI's transcoder tables give an encoding. The Windows and Macintosh code pages
        /// are spelled in upper case there while Encoding#name spells them in mixed case, and MRI
        /// compares the two spellings byte for byte when it chooses between the short and the long
        /// form of a conversion-error message, so the difference is visible from Ruby.
        /// </summary>
        public static string/*!*/ TranscoderName(RubyEncoding/*!*/ encoding) {
            string name = encoding.Name;
            return name.StartsWith("Windows-", StringComparison.Ordinal) || name.StartsWith("mac", StringComparison.Ordinal)
                ? name.ToUpperInvariant() : name;
        }

        /// <summary>
        /// MRI names only the failing leg of a conversion when that leg is the whole conversion,
        /// and otherwise spells out the entire path: "U+65E5 from UTF-8 to EUC-JP" but "U+65E5 to
        /// US-ASCII in conversion from UTF-16BE to UTF-8 to US-ASCII".  <paramref name="stage"/> is
        /// the index in <paramref name="path"/> of the leg that failed.
        /// </summary>
        public static string/*!*/ UndefinedConversionMessage(string/*!*/ dumped, RubyEncoding/*!*/[]/*!*/ path, int stage) {
            string stageSource = TranscoderName(path[stage]);
            string stageDestination = TranscoderName(path[stage + 1]);

            if (stageSource == path[0].Name && stageDestination == path[path.Length - 1].Name) {
                return FormatMessage("{0} from {1} to {2}", dumped, stageSource, stageDestination);
            }

            var text = new StringBuilder(dumped);
            text.Append(" to ").Append(stageDestination).Append(" in conversion from ").Append(path[0].Name);
            for (int i = 1; i < path.Length; i++) {
                text.Append(" to ").Append(TranscoderName(path[i]));
            }
            return text.ToString();
        }

        /// <summary>
        /// The error MRI raises for a character no leg of the conversion can represent.
        /// <paramref name="dumped"/> is how MRI spells the character: U+XXXX when the failing leg
        /// reads UTF-8 and can therefore talk about code points, and an inspected byte string when
        /// it cannot.
        /// </summary>
        public static Exception/*!*/ CreateUndefinedConversionError(string/*!*/ dumped, byte[] errorCharBytes,
            RubyEncoding/*!*/[]/*!*/ path, int stage) {

            var error = new UndefinedConversionError(UndefinedConversionMessage(dumped, path, stage));
            error.SourceEncoding = path[stage];
            error.DestinationEncoding = path[stage + 1];
            error.ErrorCharBytes = errorCharBytes;
            return error;
        }

        public static Exception/*!*/ CreateTranscodingError(EncoderFallbackException/*!*/ e, RubyEncoding/*!*/ fromEncoding, RubyEncoding/*!*/ toEncoding) {
            return new UndefinedConversionError(
                FormatMessage(
                    "\"{0}\" to UTF-8 in conversion from {1} to UTF-8 to {2}",
                    e.CharUnknown,
                    fromEncoding,
                    toEncoding
                )
            );
        }

        public static Exception/*!*/ CreateTranscodingError(DecoderFallbackException/*!*/ e, RubyEncoding/*!*/ fromEncoding, RubyEncoding/*!*/ toEncoding) {
            throw new UndefinedConversionError(
                FormatMessage(
                    "\"{0}\" to {2} in conversion from {1} to UTF-8 to {2}",
                    BitConverter.ToString(e.BytesUnknown),
                    fromEncoding,
                    toEncoding
                )
            );        
        }

        #endregion

        #region MissingMethodException

        private static Exception/*!*/ CreateMethodMissing(string/*!*/ message) {
            return new MissingMethodException(message);
        }

        public static Exception/*!*/ CreateMethodMissing(RubyContext/*!*/ context, object self, string/*!*/ name) {
            return WithNameAndReceiver(context, CreateMethodMissing(FormatMethodMissingMessage(context, self, name)), name, self);
        }

        public static Exception/*!*/ CreateUndefinedLocalOrMethod(RubyContext/*!*/ context, object self, string/*!*/ name) {
            return WithNameAndReceiver(context,
                CreateNameError(FormatMethodMissingMessage(context, self, name, "undefined local variable or method '{0}' for {1}")), name, self);
        }

        public static Exception/*!*/ CreatePrivateMethodCalled(RubyContext/*!*/ context, object self, string/*!*/ name) {
            return WithNameAndReceiver(context,
                CreateMethodMissing(FormatMethodMissingMessage(context, self, name, "private method '{0}' called for {1}")), name, self);
        }

        public static Exception/*!*/ CreateProtectedMethodCalled(RubyContext/*!*/ context, object self, string/*!*/ name) {
            return WithNameAndReceiver(context,
                CreateMethodMissing(FormatMethodMissingMessage(context, self, name, "protected method '{0}' called for {1}")), name, self);
        }

        public static Exception/*!*/ CreateNoSuperclassMethod(RubyContext/*!*/ context, object self, string/*!*/ name) {
            return WithNameAndReceiver(context,
                CreateMethodMissing(FormatMethodMissingMessage(context, self, name, "super: no superclass method '{0}' for {1}")), name, self);
        }

        public static string/*!*/ FormatMethodMissingMessage(RubyContext/*!*/ context, object self, string/*!*/ name) {
            return FormatMethodMissingMessage(context, self, name, "undefined method '{0}' for {1}");
        }

        [ThreadStatic]
        private static bool _disableMethodMissingMessageFormatting;

        /// <summary>
        /// True for values whose #inspect is too expensive to be worth putting in an error message.
        /// Only Bignums qualify today: BigInteger.ToString is quadratic in the number of digits.
        /// </summary>
        private static bool IsTooBigToInspect(object/*!*/ obj) {
            return obj is BigInteger big && big.GetBitLength() > 4096;
        }

        internal static string/*!*/ FormatMethodMissingMessage(RubyContext/*!*/ context, object obj, string/*!*/ name, string/*!*/ message) {
            Assert.NotNull(name);

            return FormatMessage(message, name, FormatMethodMissingReceiver(context, obj));
        }

        /// <summary>
        /// How MRI describes the receiver of a NameError/NoMethodError. Verified against CRuby 3.3.8:
        ///   nil / true / false          => "nil" / "true" / "false"
        ///   a class / a module          => "class String" / "module Enumerable"
        ///   an object with a singleton  => its #inspect  ("#&lt;Object:0x...&gt;", "main")
        ///   anything else               => "an instance of Object"
        /// (IronRuby used to print "#&lt;Object:0x...&gt;" and "nil:NilClass" for all of these.)
        /// </summary>
        internal static string/*!*/ FormatMethodMissingReceiver(RubyContext/*!*/ context, object obj) {
            if (obj == null) {
                return "nil";
            }
            if (obj is bool) {
                return (bool)obj ? "true" : "false";
            }

            var module = obj as RubyModule;
            if (module != null) {
                string kind = module.IsClass ? "class " : "module ";
                string moduleName = RubyName(context, module);
                return kind + (String.IsNullOrEmpty(moduleName) ? SafeInspect(context, obj) : moduleName);
            }

            // An object that carries a singleton class is spelled the way the default #to_s would
            // (rb_any_to_s: "#<Object:0x...>"), whatever #inspect it defines; the top-level object
            // is "main".
            RubyClass immediate = context.GetImmediateClassOf(obj);
            if (immediate != null && immediate.IsSingletonClass) {
                var globalScope = context.MainGlobalScope;
                if (globalScope != null && ReferenceEquals(globalScope.MainObject, obj)) {
                    return "main";
                }
                return RubyUtils.ObjectToMutableString(context, obj).ToString();
            }

            // An anonymous class has no name; MRI then shows how the class inspects
            // ("#<Class:0x...>", or whatever inspect it defines).
            var cls = context.GetClassOf(obj);
            string className = RubyName(context, cls);
            if (String.IsNullOrEmpty(className)) {
                className = SafeInspect(context, cls);
            }
            return "an instance of " + MessageTypeName(className);
        }

        private static CallSite<Func<CallSite, object, object>> _NameSite;

        /// <summary>
        /// What the module answers to #name, which is what MRI builds this message out of - a
        /// class can define its own #name and MRI shows that one. The built-in #name is not
        /// asked, because IronRuby's disambiguates a module belonging to another runtime by
        /// appending "@&lt;runtime id&gt;" and that has no place in this message; the name the module
        /// was defined under is used directly instead. nil - what an anonymous module answers -
        /// means "no name", and the caller falls back to how the module inspects.
        /// </summary>
        private static string RubyName(RubyContext/*!*/ context, RubyModule/*!*/ module) {
            // Already inside a message being built: asking again could recurse for ever.
            if (_disableMethodMissingMessageFormatting) {
                return module.Name;
            }

            var resolved = context.ResolveMethod(module, "name", VisibilityContext.AllVisible);
            if (!resolved.Found || resolved.Info.DeclaringModule == context.ModuleClass) {
                return module.Name;
            }

            _disableMethodMissingMessageFormatting = true;
            try {
                var site = RubyUtils.GetCallSite(ref _NameSite, context, "name", 0);
                var name = site.Target(site, module) as MutableString;
                return (name != null) ? name.ConvertToString() : null;
            } catch (Exception) {
                // MRI swallows whatever a user-written #name does here.
                return module.Name;
            } finally {
                _disableMethodMissingMessageFormatting = false;
            }
        }

        private static string/*!*/ SafeInspect(RubyContext/*!*/ context, object obj) {
            if (IsTooBigToInspect(obj)) {
                // Rendering a multi-megabit Bignum in base 10 is quadratic:
                // (2 ** 40_000_000).no_such_method spent minutes building a message nobody
                // would read. MRI 3.4 dropped the value from this message entirely; naming
                // the class is the closest we get without changing the common case.
                return context.GetClassName(obj);
            }
            if (_disableMethodMissingMessageFormatting) {
                return RubyUtils.ObjectToMutableString(context, obj).ToString();
            }
            _disableMethodMissingMessageFormatting = true;
            try {
                return context.Inspect(obj).ConvertToString();
            } catch (Exception) {
                // MRI: swallows all exceptions
                return RubyUtils.ObjectToMutableString(context, obj).ToString();
            } finally {
                _disableMethodMissingMessageFormatting = false;
            }
        }

        #endregion

        #region LoadError

        public static Exception/*!*/ CreateLoadError(Exception/*!*/ innerException) {
            return new LoadError(innerException.Message, innerException);
        }

        public static Exception/*!*/ CreateLoadError(string/*!*/ message) {
            return new LoadError(message);
        }

        #endregion

        public static Exception/*!*/ CreateNotImplementedError(string/*!*/ message, params object[] args) {
            return new NotImplementedError(FormatMessage(message, args));
        }

        public static Exception/*!*/ CreateIndexError(string/*!*/ message, params object[] args) {
            return new IndexError(FormatMessage(message, args));
        }

        public static Exception/*!*/ CreateRangeError(string/*!*/ message, params object[] args) {
            return CreateRangeError("", message, args);
        }

        public static Exception/*!*/ CreateRangeError(string/*!*/ paramName, string/*!*/ message, params object[] args) {
            return new ArgumentOutOfRangeException(paramName, FormatMessage(message, args));
        }

        public static Exception/*!*/ CreateLocalJumpError(string/*!*/ message, params object[] args) {
            return new LocalJumpError(FormatMessage(message, args));
        }

        public static Exception/*!*/ CreateRuntimeError(string/*!*/ message, params object[] args) {
            return new RuntimeError(FormatMessage(message, args));
        }

        public static Exception/*!*/ NoBlockGiven() {
            return CreateLocalJumpError("no block given (yield)");
        }

        public static Exception/*!*/ CreateIOError(string/*!*/ message) {
            return new IOException(message);
        }

        public static Exception/*!*/ CreateSystemCallError(string/*!*/ message, params object[] args) {
            return new ExternalException(FormatMessage(message, args));
        }

        public static Exception/*!*/ CreateSecurityError(string/*!*/ message, params object[] args) {
            throw new SecurityException(FormatMessage(message, args));
        }

        public static Exception/*!*/ CreateEncodingCompatibilityError(RubyEncoding/*!*/ encoding1, RubyEncoding/*!*/ encoding2) {
            return new EncodingCompatibilityError(FormatMessage("incompatible character encodings: {0} and {1}", encoding1.Name, encoding2.Name));
        }

        /// <summary>
        /// MRI's rb_must_asciicompat: a string that is to be read as a number (or a format) has to
        /// be in an ASCII-compatible encoding.
        /// </summary>
        public static Exception/*!*/ CreateAsciiIncompatibleEncodingError(RubyEncoding/*!*/ encoding) {
            return new EncodingCompatibilityError(FormatMessage("ASCII incompatible encoding: {0}", encoding.Name));
        }

        #region Errno

        // TODO: initialize errno property

        public static string/*!*/ MakeMessage(string message, string/*!*/ baseMessage) {
            Assert.NotNull(baseMessage);
            if (message == null) {
                return baseMessage;
            }
            // A message that already leads with the error's own text ("No such file or directory
            // - path", as several callers build it) is complete: MRI never says it twice.
            if (message.StartsWith(baseMessage, StringComparison.Ordinal)) {
                string rest = message.Substring(baseMessage.Length);
                if (rest.Length == 0 || rest.StartsWith(" - ", StringComparison.Ordinal) || rest.StartsWith(" @ ", StringComparison.Ordinal)) {
                    return message;
                }
            }
            return String.Concat(baseMessage, " - ", message);
        }

        public static string/*!*/ MakeMessage(ref MutableString message, string/*!*/ baseMessage) {
            Assert.NotNull(baseMessage);
            string result = MakeMessage(message != null ? message.ConvertToString() : null, baseMessage);
            message = MutableString.Create(result, message != null ? message.Encoding : RubyEncoding.UTF8);
            return result;
        }

        public static Exception/*!*/ CreateEEXIST() {
            return new ExistError();
        }

        public static Exception/*!*/ CreateEEXIST(string message, params object[] args) {
            return CreateEEXIST(null, message, args);
        }

        public static Exception/*!*/ CreateEEXIST(Exception inner, string/*!*/ message, params object[] args) {
            return new ExistError(FormatMessage(message, args), inner);
        }

        public static Exception/*!*/ CreateEINVAL() {
            return new InvalidError();
        }

        public static Exception/*!*/ CreateEINVAL(string/*!*/ message, params object[] args) {
            return CreateEINVAL(null, message, args);
        }

        public static Exception/*!*/ CreateEINVAL(Exception inner, string/*!*/ message, params object[] args) {
            return new InvalidError(FormatMessage(message, args), inner);
        }

        public static Exception/*!*/ CreateENOENT() {
            return new FileNotFoundException("No such file or directory");
        }

        public static Exception/*!*/ CreateENOENT(string/*!*/ message, params object[] args) {
            return CreateENOENT(null, message, args);
        }

        public static Exception/*!*/ CreateENOENT(Exception inner, string/*!*/ message, params object[] args) {
            return new FileNotFoundException(FormatMessage(message, args), inner);
        }

        public static Exception/*!*/ CreateEBADF() {
            return new BadFileDescriptorError();
        }

        public static Exception/*!*/ CreateENXIO(string/*!*/ path) {
            return new NoSuchDeviceOrAddressError(path);
        }

        public static Exception/*!*/ CreateEACCES() {
            return new UnauthorizedAccessException();
        }

        /// <summary>The constructor already prefixes "Is a directory - ", so pass only the path.</summary>
        public static Exception/*!*/ CreateEISDIR(string/*!*/ path) {
            return new DirectoryIsError(path);
        }

        #endregion
    }
}

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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Dynamic;
using System.Diagnostics;
using System.Reflection;
using IronRuby.Compiler;
using IronRuby.Compiler.Generation;

namespace IronRuby.Builtins {
    using Ast = Expression;
    using AstFactory = IronRuby.Compiler.Ast.AstFactory;
    using AstUtils = Microsoft.Scripting.Ast.Utils;

    // Exception
    // -- fatal
    // -- NoMemoryError                                 
    // -- ScriptError
    // ---- LoadError
    // ---- NotImplementedError
    // ---- SyntaxError
    // -- SignalException (not supported)
    // ---- Interrupt (not supported)
    // -- StandardError
    // ---- ArgumentError
    // ---- IOError
    // ------ EOFError
    // ---- IndexError
    // ---- LocalJumpError
    // ---- NameError
    // ------ NoMethodError
    // ---- RangeError
    // ------ FloatDomainError
    // ---- RegexpError
    // ---- RuntimeError
    // ---- SecurityError
    // ---- SystemCallError
    // ------ system-dependent-exceptions Errno::XXX
    // ---- SystemStackError
    // ---- ThreadError
    // ---- TypeError
    // ---- ZeroDivisionError
    // ---- EncodingError (1.9)
    // ------ Encoding::CompatibilityError (1.9)
    // ------ Encoding::UndefinedConversionError (1.9)
    // ------ Encoding::InvalidByteSequenceError (1.9)
    // ------ Encoding::ConverterNotFoundError (1.9)
    // -- SystemExit
    [RubyException("Exception", Extends = typeof(Exception))]
    public static class ExceptionOps {

        #region Construction

        [Emitted]
        public static string/*!*/ GetClrMessage(RubyClass/*!*/ exceptionClass, object message) {
            return RubyExceptionData.GetClrMessage(exceptionClass.Context, message);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static Exception/*!*/ ReinitializeException(RubyContext/*!*/ context, Exception/*!*/ self, [DefaultParameterValue(null)]object message) {
            var instance = RubyExceptionData.GetInstance(self);
            instance.Backtrace = null;
            instance.Message = message ?? RubyExceptionData.GetDefaultMessage(context.GetClassOf(self));
            return self;
        }

        [RubyMethod("exception", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ CreateException() {
            return new RuleGenerator(RuleGenerators.InstanceConstructor);
        }

        /// <summary>
        /// #dup and #clone route here. Object#initialize_copy copies the instance variables;
        /// an exception's message, backtrace and cause live outside them, in RubyExceptionData.
        /// </summary>
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static Exception/*!*/ InitializeCopy(RubyContext/*!*/ context, Exception/*!*/ self, [NotNull]Exception/*!*/ source) {
            KernelOps.InitializeCopy(context, self, source);

            var sourceData = RubyExceptionData.GetInstance(source);
            var selfData = RubyExceptionData.GetInstance(self);
            selfData.Message = sourceData.Message;
            selfData.Backtrace = sourceData.Backtrace;
            if (sourceData.HasCause) {
                selfData.TrySetCause(sourceData.Cause);
            }
            // NameError#name / #receiver and NoMethodError#args (#dup has to keep them)
            selfData.Name = sourceData.Name;
            if (sourceData.HasReceiver) {
                selfData.SetReceiver(sourceData.Receiver);
            }
            selfData.Arguments = sourceData.Arguments;
            return self;
        }

        #endregion

        #region Public Instance Methods

        [RubyMethod("backtrace", RubyMethodAttributes.PublicInstance)]
        public static RubyArray GetBacktrace(Exception/*!*/ self) {
            return RubyExceptionData.GetInstance(self).Backtrace;
        }

        /// <summary>
        /// The exception that was being handled ($!) when this one was raised, or nil.
        /// Assigned once, by Kernel#raise; see RubyExceptionData.TrySetCause.
        /// </summary>
        [RubyMethod("cause", RubyMethodAttributes.PublicInstance)]
        public static Exception GetCause(Exception/*!*/ self) {
            return RubyExceptionData.GetInstance(self).Cause;
        }

        [RubyMethod("set_backtrace", RubyMethodAttributes.PublicInstance)]
        public static RubyArray/*!*/ SetBacktrace(Exception/*!*/ self, [NotNull]MutableString/*!*/ backtrace) {
            return RubyExceptionData.GetInstance(self).Backtrace = RubyArray.Create(backtrace);
        }
        
        [RubyMethod("set_backtrace", RubyMethodAttributes.PublicInstance)]
        public static RubyArray SetBacktrace(Exception/*!*/ self, RubyArray backtrace) {
            if (backtrace != null && !CollectionUtils.TrueForAll(backtrace, (item) => item is MutableString)) {
                throw RubyExceptions.CreateTypeError("backtrace must be Array of String");
            }

            return RubyExceptionData.GetInstance(self).Backtrace = backtrace;
        }

        [RubyMethod("set_backtrace", RubyMethodAttributes.PublicInstance)]
        public static RubyArray SetBacktrace(Exception/*!*/ self, object backtrace) {
            // anything but nil, a String or an Array of Strings
            throw RubyExceptions.CreateTypeError("backtrace must be Array of String");
        }

        /// <summary>
        /// MRI's Exception#exception: self when called with no argument or with self, otherwise a
        /// *clone* of self carrying the new message. Cloning rather than allocating a fresh
        /// instance matters - #initialize is not run again, so a subclass that takes something
        /// other than a message in its constructor keeps its state (and any shared mutable state
        /// stays shared, which ruby/spec relies on).
        /// </summary>
        [RubyMethod("exception", RubyMethodAttributes.PublicInstance)]
        public static object GetException(UnaryOpStorage/*!*/ cloneStorage, RubyContext/*!*/ context, Exception/*!*/ self,
            [Optional]object message) {

            if (message == Missing.Value || ReferenceEquals(message, self)) {
                return self;
            }

            var site = cloneStorage.GetCallSite("clone");
            var copy = site.Target(site, self) as Exception;
            if (copy == null) {
                throw RubyExceptions.CreateTypeError("exception object expected");
            }

            // unlike #initialize this leaves the backtrace alone, as MRI's clone does
            RubyExceptionData.GetInstance(copy).Message =
                message ?? RubyExceptionData.GetDefaultMessage(context.GetClassOf(copy));
            return copy;
        }

        [RubyMethod("message")]
        public static object GetMessage(UnaryOpStorage/*!*/ stringReprStorage, Exception/*!*/ self) {
            var site = stringReprStorage.GetCallSite("to_s");
            return site.Target(site, self);
        }

        [RubyMethod("to_s")]
        public static object StringRepresentation(ConversionStorage<MutableString>/*!*/ strTryCast, ConversionStorage<MutableString>/*!*/ tosConversion, Exception/*!*/ self) {
            object message = RubyExceptionData.GetInstance(self).Message;
            if (message is MutableString) {
                return message;
            }
            // MRI applies String() to whatever was passed as the message: #to_str when the
            // object offers one, #to_s otherwise.
            MutableString str = Protocols.TryCastToString(strTryCast, message);
            return str ?? (object)Protocols.ConvertToString(tosConversion, message);
        }

        /// <summary>
        /// MRI compares the class, the message and the backtrace - never object identity, so a
        /// #dup is == to its original. The message is read out of the exception's own state
        /// rather than through #message, which subclasses are free to override (and
        /// ExceptionSpecs::UnExceptional does).
        /// </summary>
        [RubyMethod("==")]
        public static bool Equal(BinaryOpStorage/*!*/ equals, RubyContext/*!*/ context, Exception/*!*/ self, object other) {
            if (ReferenceEquals(self, other)) {
                return true;
            }

            var otherException = other as Exception;
            if (otherException == null) {
                return false;
            }

            if (context.GetClassOf(self).GetNonSingletonClass() != context.GetClassOf(otherException).GetNonSingletonClass()) {
                return false;
            }

            var selfData = RubyExceptionData.GetInstance(self);
            var otherData = RubyExceptionData.GetInstance(otherException);
            return Protocols.IsEqual(equals, selfData.Message, otherData.Message)
                && Protocols.IsEqual(equals, selfData.Backtrace, otherData.Backtrace);
        }

        [RubyMethod("inspect", RubyMethodAttributes.PublicInstance)]
        public static MutableString/*!*/ Inspect(UnaryOpStorage/*!*/ inspectStorage, UnaryOpStorage/*!*/ toSStorage,
            ConversionStorage<MutableString>/*!*/ tosConversion, Exception/*!*/ self) {

            var context = inspectStorage.Context;
            // MRI goes through #to_s here, so an override of it shows up in #inspect
            var toSSite = toSStorage.GetCallSite("to_s");
            object message = toSSite.Target(toSSite, self);

            // an anonymous class has no name; MRI prints "#<#<Class:0x...>: msg>" for it
            MutableString className = RubyExceptionData.GetDefaultMessage(context.GetClassOf(self));

            var messageString = message as MutableString;
            if (message == null || (messageString != null && messageString.IsEmpty)) {
                // MRI drops the "#<...>" wrapper entirely when #to_s is empty
                return className;
            }

            MutableString result = MutableString.CreateMutable(context.GetIdentifierEncoding());
            result.Append("#<");
            result.Append(className);
            if (messageString != null && messageString.IndexOf('\n') >= 0) {
                // Ruby 3.4: a message that spans lines is shown quoted, so the inspect of an
                // exception stays on one line - #<RuntimeError:"a\nb">, with no space.
                var inspectSite = inspectStorage.GetCallSite("inspect");
                result.Append(':');
                result.Append(Protocols.ConvertToString(tosConversion, inspectSite.Target(inspectSite, messageString)));
            } else {
                result.Append(": ");
                result.Append(KernelOps.Inspect(inspectStorage, tosConversion, message));
            }
            result.Append('>');
            return result;
        }

        #endregion        
    }
}

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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Interpreter;
using Microsoft.Scripting.Utils;
using System.Globalization;

namespace IronRuby.Runtime {
    /// <summary>
    /// Stores extra instance data associated with Ruby exceptions
    /// </summary>
    [Serializable]
    public sealed class RubyExceptionData {
        private static readonly object/*!*/ _DataKey = typeof(RubyExceptionData);

        // An exception class can implement singleton method "new" that returns an arbitrary instance of an exception.
        // This mapping needs to be applied on exceptions created in libraries as well (they should be created "dynamically").
        // That would however need to pass RubyContext to every method that might throw an exception. Instead, we call
        // "new" on the exception's class as soon as it gets to the first Ruby EH handler (rescue/ensure/else).
        //
        // True if the exception has already been handled by Ruby EH clause or if it was constructed "dynamically" via Class#new.
        internal bool Handled { get; set; }

        // Real exception begin propagated by the CLR. Needed for lazy initialization of message, backtrace
        private Exception/*!*/ _exception;
        // For asynchronous exceptions (Thread#raise), the user-visible exception (accessible via _visibleException)
        // is wrapped in a TheadAbortException (accessible via _exception)
        private Exception/*!*/ _visibleException;

#if DEBUG && FEATURE_THREAD
#pragma warning disable 414 // msc: unused field
        // For asynchronous exceptions, this is useful to figure out which thread raised the exception
        [NonSerialized]
        private Thread/*!*/ _throwingThread;
#pragma warning restore 414
#endif

        // if this is set to null we need to initialize it
        private object _message; 
        private static readonly string _DefaultDivideByZeroMessage = new DivideByZeroException().Message;
        
        // can be set explicitly by the user (even to nil):
        private RubyArray _backtrace;

#if FEATURE_STACK_TRACE
        // The backtrace the runtime captured when the exception was raised, until someone asks for
        // it (see CaptureExceptionTrace). Only ever set while _backtrace is null.
        [NonSerialized]
        private RubyStackTraceBuilder.CapturedTrace _capturedTrace;

        // The catch-site walk of the interpreted frame that last caught an exception on this thread.
        [ThreadStatic]
        private static WeakReference<InterpretedFrame> _lastCatchFrame;
        [ThreadStatic]
        private static StackTrace _lastCatchSiteTrace;
#endif

        // Exception#cause; _hasCause tells "decided" from "not raised yet", see TrySetCause.
        private Exception _cause;
        private bool _hasCause;

        // NameError#name / NameError#receiver. Both NameError and NoMethodError are backed by
        // framework exception types (MemberAccessException, MissingMethodException) that cannot
        // carry extra fields, so they live here alongside the message and backtrace.
        private object _name;
        private object _receiver;
        private bool _hasReceiver;

        // NoMethodError#args: the arguments of the call that found no method (nil if unknown).
        private object _arguments;

        [NonSerialized]
        private CallSite<Func<CallSite, RubyContext, Exception, RubyArray, object>> _setBacktraceCallSite;

        private RubyExceptionData(Exception/*!*/ exception) {
            _exception = exception;
            _visibleException = exception;
#if DEBUG && FEATURE_THREAD
            _throwingThread = Thread.CurrentThread;
#endif
        }

        public static RubyArray/*!*/ CreateBacktrace(RubyContext/*!*/ context, int skipFrames) {
#if FEATURE_STACK_TRACE
            return new RubyStackTraceBuilder(context, skipFrames).RubyTrace;
#else
            return new RubyArray();
#endif
        }

        /// <summary>
        /// Like CreateBacktrace, but frames of core methods written in Ruby keep their own file and line
        /// instead of being reported at their caller's (TracePoint needs to see where a call really is).
        /// </summary>
        internal static RubyArray/*!*/ CreateRawBacktrace(RubyContext/*!*/ context) {
#if FEATURE_STACK_TRACE
            return new RubyStackTraceBuilder(context, 0, true).RubyTrace;
#else
            return new RubyArray();
#endif
        }

        /// <summary>
        /// The backtrace of a thread other than the calling one, or null if that thread has no Ruby
        /// stack to speak of - it has not started, it has finished, or it has never run Ruby code.
        /// </summary>
        public static RubyArray CreateBacktrace(RubyContext/*!*/ context, Thread/*!*/ thread) {
#if FEATURE_STACK_TRACE && FEATURE_THREAD
            if (thread == Thread.CurrentThread) {
                return CreateBacktrace(context, 0);
            }

            InterpretedFrame frame;
            if (!InterpretedFrame.CurrentFrame.TryGetValue(thread, out frame) || frame == null) {
                return null;
            }

            return new RubyStackTraceBuilder(context, frame).RubyTrace;
#else
            return null;
#endif
        }

        /// <summary>
        /// Builds backtrace for the exception if it wasn't built yet. 
        /// Captures a full stack trace starting with the current frame and combines it with the trace of the exception.
        /// Called from compiled code.
        /// </summary>
        internal void CaptureExceptionTrace(RubyScope/*!*/ scope) {
            if (!HasBacktrace) {
                var context = scope.RubyContext;
#if FEATURE_STACK_TRACE
                bool debugMode = context.DomainManager.Configuration.DebugMode;

                // The catch-site walk is the expensive part of a raise: the whole CLR stack, every frame's
                // method resolved. All but its top - the frames this one sees the exception from - lies
                // below the catching frame, and nothing below a frame changes while the frame lives. So an
                // interpreted frame that keeps catching (a loop around begin/rescue) reuses the walk it
                // took the first time. Interpreted frames because their identity is that of a single
                // invocation (a scope is not: eval code runs in the scope of its binding); not in the modes
                // that report CLR frames, which would show the top of the walk too.
                StackTrace catchSiteTrace = null;
                InterpretedFrame frame = scope.InterpretedFrame;
                WeakReference<InterpretedFrame> lastFrame = _lastCatchFrame;
                bool reusable = frame != null && !debugMode && !context.Options.ExceptionDetail &&
                    frame == InterpretedFrame.CurrentFrame.Value;

                InterpretedFrame last;
                if (reusable && lastFrame != null && lastFrame.TryGetTarget(out last) && last == frame) {
                    catchSiteTrace = _lastCatchSiteTrace;
                }

                if (catchSiteTrace == null) {
                    catchSiteTrace = RubyStackTraceBuilder.GetClrStackTrace(null, debugMode);
                    if (reusable && RubyStackTraceBuilder.IsReusableCatchSiteTrace(catchSiteTrace)) {
                        _lastCatchSiteTrace = catchSiteTrace;
                        if (lastFrame != null) {
                            lastFrame.SetTarget(frame);
                        } else {
                            _lastCatchFrame = new WeakReference<InterpretedFrame>(frame);
                        }
                    }
                }

                // formatted on first use, see Backtrace:
                _capturedTrace = new RubyStackTraceBuilder.CapturedTrace(context, _exception, catchSiteTrace, scope.InterpretedFrame != null);
#else
                _backtrace = new RubyArray();
#endif
                // MRI (setup_exception) only calls #set_backtrace when it has been redefined:
                if (!HasBuiltinSetBacktrace(context)) {
                    DynamicSetBacktrace(context, Backtrace);
                }
            }
        }

        /// <summary>
        /// Whether #set_backtrace of the exception is IronRuby's own: the library method, or the
        /// prelude's (Src/StdLib/ironruby/ruby4.rb) which keeps Thread::Backtrace::Locations around
        /// and passes strings on to the library method. Called with the backtrace the runtime has just
        /// captured, the prelude's would only parse the strings into Locations eagerly - which its
        /// #backtrace_locations does on demand all the same.
        /// </summary>
        private bool HasBuiltinSetBacktrace(RubyContext/*!*/ context) {
            var method = context.GetImmediateClassOf(_exception).ResolveMethod("set_backtrace", VisibilityContext.AllVisible).Info;
            if (method is RubyLibraryMethodInfo) {
                return true;
            }

            var rubyMethod = method as RubyMethodInfo;
            return rubyMethod != null && rubyMethod.Document != null && RubyStackTraceBuilder.IsInternalFile(rubyMethod.Document.FileName);
        }

        /// <summary>
        /// This is called by the IronRuby runtime to set the backtrace for an exception that has being raised. 
        /// Note that the backtrace may be set directly by user code as well. However, that uses a different code path.
        /// </summary>
        private void DynamicSetBacktrace(RubyContext/*!*/ context, RubyArray backtrace) {
            if (_setBacktraceCallSite == null) {
                Interlocked.CompareExchange(ref _setBacktraceCallSite, CallSite<Func<CallSite, RubyContext, Exception, RubyArray, object>>.
                    Create(RubyCallAction.MakeShared("set_backtrace", RubyCallSignature.WithImplicitSelf(1))), null);
            }
            _setBacktraceCallSite.Target(_setBacktraceCallSite, context, _exception, backtrace);
        }

        /// <summary>
        /// Gets the instance data associated with the exception
        /// </summary>
        public static RubyExceptionData/*!*/ GetInstance(Exception/*!*/ e) {
            RubyExceptionData result = TryGetInstance(e);
            if (result == null) {
                result = AssociateInstance(e);                
            }
            return result;
        }

        internal static RubyExceptionData/*!*/ AssociateInstance(Exception/*!*/ e) {
            RubyExceptionData result;

#if FEATURE_THREAD
            Exception visibleException = RubyUtils.GetVisibleException(e);
            if (e == visibleException || visibleException == null) {
                result = new RubyExceptionData(e);
            } else {
                // Async exception

                Debug.Assert(e is ThreadAbortException);
                result = GetInstance(visibleException);

                if (result._exception == visibleException) {
                    // A different instance of ThreadAbortException is thrown at the end of every catch block (as long as
                    // Thread.ResetAbort is not called). However, we only want to remember the first one 
                    // as it will have the most complete stack trace.
                    result._exception = e;
                }
            }
#else
            result = new RubyExceptionData(e);
#endif

            e.SetData(_DataKey, result);
            return result;
        }

        internal static RubyExceptionData TryGetInstance(Exception/*!*/ e) {
            return e.GetData(_DataKey) as RubyExceptionData;
        }
        
        public object Message {
            get {
                if (_message == null) {
                    // A Ruby-defined exception class whose #initialize never set a message: MRI's
                    // default is the class name, not the CLR's "Exception of type X was thrown."
                    var rubyObject = _visibleException as IRubyObject;
                    if (rubyObject != null) {
                        var immediateClass = rubyObject.ImmediateClass;
                        if (immediateClass != null) {
                            return _message = GetDefaultMessage(immediateClass.NominalClass);
                        }
                    }
                    string clrMessage = _visibleException.Message;
                    if (_visibleException is DivideByZeroException && clrMessage == _DefaultDivideByZeroMessage) {
                        // Integer division by zero raises the CLR's own exception, whose message is
                        // "Attempted to divide by zero."; MRI says "divided by 0".
                        clrMessage = "divided by 0";
                    }
                    _message = MutableString.Create(clrMessage, RubyEncoding.UTF8);
                }
                return _message;
            }
            set { 
                ContractUtils.RequiresNotNull(value, "value"); 
                _message = value; 
            }
        }

        public RubyArray Backtrace {
            get {
#if FEATURE_STACK_TRACE
                if (_capturedTrace != null) {
                    FormatCapturedTrace();
                }
#endif
                return _backtrace;
            }
            set {
#if FEATURE_STACK_TRACE
                _capturedTrace = null;
#endif
                _backtrace = value;
            }
        }

        /// <summary>
        /// Whether the exception has a backtrace, without formatting a captured one.
        /// </summary>
        public bool HasBacktrace {
            get {
#if FEATURE_STACK_TRACE
                return _backtrace != null || _capturedTrace != null;
#else
                return _backtrace != null;
#endif
            }
        }

#if FEATURE_STACK_TRACE
        private void FormatCapturedTrace() {
            var captured = _capturedTrace;
            if (captured == null) {
                return;
            }

            // another thread may ask for the same exception's backtrace (Thread#value, #join)
            lock (captured) {
                if (_capturedTrace == captured) {
                    _backtrace = captured.Build();
                    _capturedTrace = null;
                }
            }
        }
#endif

        /// <summary>
        /// Exception#cause. MRI fixes the cause the first time an exception is raised and never
        /// changes it afterwards, so re-raising a rescued exception keeps the original chain.
        /// <see cref="HasCause"/> distinguishes "never raised / cause not decided yet" from an
        /// explicit `cause: nil`; both answer nil from Ruby.
        /// </summary>
        public Exception Cause {
            get { return _cause; }
        }

        public bool HasCause {
            get { return _hasCause; }
        }

        /// <summary>
        /// Assigns the cause if it has not been assigned yet. Returns true if it was assigned now.
        /// </summary>
        /// <summary>
        /// NameError#name: the symbol, string or constant name the error is about. nil if unknown.
        /// </summary>
        public object Name {
            get { return _name; }
            set { _name = value; }
        }

        /// <summary>
        /// NameError#receiver: the object the missing name was looked up on. Unset (rather than
        /// nil) when the error was built without one - MRI raises ArgumentError for that.
        /// </summary>
        public object Receiver {
            get { return _receiver; }
        }

        public bool HasReceiver {
            get { return _hasReceiver; }
        }

        public void SetReceiver(object receiver) {
            _receiver = receiver;
            _hasReceiver = true;
        }

        /// <summary>
        /// NoMethodError#args. MRI records the arguments of the failed call on the error.
        /// </summary>
        // NoMethodError#private_call?: the call that went missing had an implicit receiver
        // (or `self.`), so a private method would have been reachable.
        private bool _privateCall;

        public bool PrivateCall {
            get { return _privateCall; }
            set { _privateCall = value; }
        }

        public object Arguments {
            get { return _arguments; }
            set { _arguments = value; }
        }

        public bool TrySetCause(Exception cause) {
            if (_hasCause) {
                return false;
            }
            SetCause(cause);
            return true;
        }

        /// <summary>
        /// Assigns the cause whether or not one has been assigned before. Only `raise ..., cause: c'
        /// does this: MRI's exc_setup_cause writes the ivar unconditionally when a cause was named,
        /// and otherwise leaves an exception that has one alone.
        /// </summary>
        public void SetCause(Exception cause) {
            _hasCause = true;
            // an exception is never its own cause (MRI: `raise e, cause: e` leaves cause nil)
            _cause = (cause == _exception || cause == _visibleException) ? null : cause;
        }

        public static string/*!*/ GetClrMessage(RubyContext/*!*/ context, object message) {
            return Protocols.ToClrStringNoThrow(context, message);
        }

        public static string/*!*/ GetClrMessage(RubyClass/*!*/ exceptionClass, object message) {
            // RubyClass.Name is null for an anonymous class, which used to make
            // `Class.new(StandardError).new` die with ArgumentNullException. MRI's default
            // message is the class's #to_s, i.e. "#<Class:0x...>" for an anonymous one.
            return GetClrMessage(exceptionClass.Context, message ?? GetDefaultMessage(exceptionClass));
        }

        /// <summary>
        /// MRI's default Exception#message: the name of the class (its #to_s).
        /// </summary>
        public static MutableString/*!*/ GetDefaultMessage(RubyClass/*!*/ exceptionClass) {
            var context = exceptionClass.Context;
            return exceptionClass.Name != null
                ? MutableString.Create(exceptionClass.Name, context.GetIdentifierEncoding())
                : exceptionClass.GetDisplayName(context, false);
        }

        public static Exception/*!*/ InitializeException(Exception/*!*/ exception, object message) {
            RubyExceptionData data = RubyExceptionData.GetInstance(exception);
            // only set it if message is non-null. Otherwise, let lazy initialization create the default message from CLR exception message
            if (message != null) {
                data.Message = message;
            }

            return exception;
        }

        internal static Exception/*!*/ HandleException(RubyContext/*!*/ context, Exception/*!*/ exception) {
            // already handled:
            var instanceData = GetInstance(exception);
            if (instanceData.Handled) {
                return exception;
            }

            RubyClass exceptionClass = context.GetClass(exception.GetType());

            // new resolves to Class#new built-in method, or to a library constructor (a C#-thrown
            // exception of such a class already carries whatever that constructor would set):
            var newMethod = exceptionClass.ImmediateClass.ResolveMethod("new", VisibilityContext.AllVisible);
            if (newMethod.Found && ((newMethod.Info.DeclaringModule == context.ClassClass && newMethod.Info is RubyCustomMethodInfo)
                || newMethod.Info is RubyLibraryMethodInfo)) {
                // initialize resolves to a built-in method:
                var initializeMethod = exceptionClass.ResolveMethod("initialize", VisibilityContext.AllVisible);
                if (initializeMethod.Found && initializeMethod.Info is RubyLibraryMethodInfo) {
                    instanceData.Handled = true;
                    return exception;
                }
            }

            object message = instanceData.Message;
            // Errno classes extend CLR exceptions of several roots (ExternalException,
            // IOException, ...), so SystemCallError membership is decided on the Ruby side.
            if (exceptionClass.IsSubclassOf(context.GetClass(typeof(System.Runtime.InteropServices.ExternalException)))) {
                message = StripSystemCallErrorPrefix(exceptionClass, message);
            }

            var site = exceptionClass.NewSite;
            Exception newException;
            try {
                newException = site.Target(site, exceptionClass, message) as Exception;
            } catch (Exception e) {
                // MRI: this can lead to stack overflow:
                return HandleException(context, e);
            }

            // MRI doesn't handle this correctly, see http://redmine.ruby-lang.org/issues/show/1886:
            if (newException == null) {
                newException = RubyExceptions.CreateTypeError("exception object expected");
            }

            var newInstanceData = GetInstance(newException);
            
            newInstanceData.Handled = true;
            newInstanceData._backtrace = instanceData._backtrace;
#if FEATURE_STACK_TRACE
            newInstanceData._capturedTrace = instanceData._capturedTrace;
#endif
            return newException;
        }

        /// <summary>
        /// A SystemCallError thrown from C# already says "&lt;strerror&gt; - &lt;detail&gt;"
        /// (RubyExceptions.MakeMessage). The Ruby-level initialize it is about to be handed to -
        /// the prelude's SystemCallError#initialize - builds that same form from the detail, so it
        /// gets the detail alone, or the error would name itself twice: "No such file or directory
        /// - No such file or directory - path". The class's strerror text is its Message constant.
        /// </summary>
        private static object StripSystemCallErrorPrefix(RubyClass/*!*/ exceptionClass, object message) {
            var text = message as MutableString;
            if (text == null) {
                return message;
            }
            for (RubyClass c = exceptionClass; c != null; c = c.SuperClass) {
                object constant;
                if (c.TryGetConstant(null, "Message", out constant)) {
                    var prefix = constant as MutableString;
                    if (prefix == null) {
                        return message;
                    }
                    string s = text.ConvertToString();
                    string p = prefix.ConvertToString();
                    if (s == p) {
                        return null;
                    }
                    if (s.StartsWith(p + " - ", StringComparison.Ordinal)) {
                        return MutableString.Create(s.Substring(p.Length + 3), text.Encoding);
                    }
                    return message;
                }
            }
            return message;
        }

        // A handled exception used to leave a pending Thread.Abort behind when Thread#raise had delivered it
        // that way, and Thread.ResetAbort cancelled it here. Thread#raise no longer aborts the target thread
        // (see RubyUtils.RaiseAsyncException) and Thread.ResetAbort only throws on .NET Core, so there is
        // nothing left to do.
        public static void ActiveExceptionHandled(Exception visibleException) {}
    }
}

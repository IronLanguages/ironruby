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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Interpreter;
using Microsoft.Scripting.Utils;

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

        // Compiled ensure clauses the exception has been let through (RubyOps.PassEnsure) and that have not run yet: Ruby code
        // that runs before the rescue clauses of an outer frame would be matched (see RubyOps.RescuePassMayTest).
        internal int PendingPassedEnsures;

        /// <summary>
        /// Whether RubyOps.CanRescue would match the rescue clauses against <paramref name="exception"/> itself: it has been
        /// handled, or HandleException would only mark it so (its class's #new and #initialize are IronRuby's own) rather than
        /// make a new one. Runs no Ruby code.
        /// </summary>
        internal static bool IsHandledInPlace(RubyContext/*!*/ context, Exception/*!*/ exception) {
            var data = TryGetInstance(exception);
            if (data != null && data.Handled) {
                return true;
            }
            return IsBuiltinConstruction(context, context.GetClass(exception.GetType()));
        }

        // #new resolves to Class#new or a library constructor, and #initialize to a library method: HandleException only marks the
        // exception handled (a C#-thrown exception of such a class already carries whatever that constructor would set)
        private static bool IsBuiltinConstruction(RubyContext/*!*/ context, RubyClass/*!*/ exceptionClass) {
            var newMethod = exceptionClass.ImmediateClass.ResolveMethod("new", VisibilityContext.AllVisible);
            if (newMethod.Found && ((newMethod.Info.DeclaringModule == context.ClassClass && newMethod.Info is RubyCustomMethodInfo)
                || newMethod.Info is RubyLibraryMethodInfo)) {
                var initializeMethod = exceptionClass.ResolveMethod("initialize", VisibilityContext.AllVisible);
                return initializeMethod.Found && initializeMethod.Info is RubyLibraryMethodInfo;
            }
            return false;
        }

        /// <summary>
        /// Set by Kernel#raise for an exception that no Ruby code can have kept before it was raised: made by the raise itself
        /// from a String, or from a class whose #exception, #new and #initialize are all IronRuby's own. Only the backtrace of such
        /// an exception may wait for it to be exposed (see TryDeferCatchSite): a kept one may be read after its rescuing frame is
        /// gone without any of that frame's code ever touching it (`raise e' rescued and dropped, e read later).
        /// </summary>
        public bool RaisedUnkept { get; set; }

        /// <summary>
        /// Whether <c>raise exceptionClass</c> makes the exception without running any Ruby code that could keep it: #exception,
        /// #new and #initialize of the class are IronRuby's own.
        /// </summary>
        public static bool MakesUnkeptException(RubyClass/*!*/ exceptionClass) {
            var singleton = exceptionClass.ImmediateClass;
            return IsBuiltin(singleton.ResolveMethod("exception", VisibilityContext.AllVisible).Info) &&
                IsBuiltin(singleton.ResolveMethod("new", VisibilityContext.AllVisible).Info) &&
                IsBuiltin(exceptionClass.ResolveMethod("initialize", VisibilityContext.AllVisible).Info);
        }

        // a method of IronRuby's own, which runs no Ruby code: a library method, or one the runtime builds (Class#new, Exception.exception)
        private static bool IsBuiltin(RubyMemberInfo method) {
            return method is RubyLibraryMethodInfo || method is RubyCustomMethodInfo;
        }

        // The exception this data belongs to. Needed for lazy initialization of message, backtrace
        private readonly Exception/*!*/ _exception;

        // if this is set to null we need to initialize it
        private object _message; 
        private static readonly string _DefaultDivideByZeroMessage = new DivideByZeroException().Message;
        
        // can be set explicitly by the user (even to nil):
        private RubyArray _backtrace;

        // The backtrace of the raise, when a #set_backtrace of the exception's own was handed it instead
        // of #backtrace (see CallRedefinedSetBacktrace); null otherwise.
        private RubyArray _raiseBacktrace;

        // The backtrace the runtime captured when the exception was raised, until someone asks for
        // it (see CaptureExceptionTrace). Only ever set while _backtrace is null.
        [NonSerialized]
        private RubyStackTraceBuilder.CapturedTrace _capturedTrace;

        // The catch-site walk of the interpreted frame that last caught an exception on this thread.
        [ThreadStatic]
        private static WeakReference<InterpretedFrame> _lastCatchFrame;
        [ThreadStatic]
        private static StackTrace _lastCatchSiteTrace;

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

        // NameError#local_variables: those of the scope the error was made in (null: none).
        private RubyArray _localVariables;

        // The frame of the method an arity error was raised for (see RubyOps.SetCalleeFrame): file null
        // for a library method, which is reported at the location of its caller.
        private string _calleeFile;
        private int _calleeLine;
        private string _calleeLabel;

        [NonSerialized]
        private CallSite<Func<CallSite, RubyContext, Exception, RubyArray, object>> _setBacktraceCallSite;

        private RubyExceptionData(Exception/*!*/ exception) {
            _exception = exception;
        }

        public static RubyArray/*!*/ CreateBacktrace(RubyContext/*!*/ context, int skipFrames) {
            return new RubyStackTraceBuilder(context, skipFrames).RubyTrace;
        }

        /// <summary>
        /// The first <paramref name="maxFrames"/> frames of what <see cref="CreateBacktrace(RubyContext, int)"/> would
        /// return, without making the frames of the rest of the stack (most of the cost on a deep stack).
        /// </summary>
        public static RubyArray/*!*/ CreateBacktrace(RubyContext/*!*/ context, int skipFrames, int maxFrames) {
            return new RubyStackTraceBuilder(context, skipFrames, false, maxFrames).RubyTrace;
        }

        /// <summary>
        /// Like CreateBacktrace, but frames of core methods written in Ruby keep their own file and line
        /// instead of being reported at their caller's (TracePoint needs to see where a call really is).
        /// </summary>
        internal static RubyArray/*!*/ CreateRawBacktrace(RubyContext/*!*/ context) {
            return new RubyStackTraceBuilder(context, 0, true).RubyTrace;
        }

        /// <summary>
        /// The backtrace of a thread other than the calling one, or null if that thread has no Ruby
        /// stack to speak of - it has not started, it has finished, or it has never run Ruby code.
        /// </summary>
        public static RubyArray CreateBacktrace(RubyContext/*!*/ context, Thread/*!*/ thread) {
            if (thread == Thread.CurrentThread) {
                return CreateBacktrace(context, 0);
            }

            InterpretedFrame frame;
            if (!InterpretedFrame.CurrentFrame.TryGetValue(thread, out frame)) {
                frame = null;
            }
            // the startup library's frames that run a block, which the interpreted chain does not have
            var precompiled = Precompiled.PrecompiledFrames.Snapshot(thread);
            if (frame == null && precompiled.Length == 0) {
                return null;
            }

            return new RubyStackTraceBuilder(context, frame, precompiled, Precompiled.PrecompiledFrames.SnapshotLoopLines(thread)).RubyTrace;
        }

        /// <summary>
        /// The innermost Ruby frame of the calling thread ("file:line:in 'name'"), or null if it has none. Read from the
        /// interpreted and compiled frames' own records, as another thread's backtrace is, not from a CLR stack walk: the
        /// walk resolves every CLR frame of the stack, the host's included, which made it most of what Thread.new cost.
        /// A library method has no frame there, so this is the frame of the Ruby code that called it.
        /// </summary>
        public static MutableString GetCallerFrame(RubyContext/*!*/ context) {
            var trace = new RubyStackTraceBuilder(context, InterpretedFrame.CurrentFrame.Value,
                Precompiled.PrecompiledFrames.Snapshot(Thread.CurrentThread), Precompiled.PrecompiledFrames.CaptureLoopLines(), 1).RubyTrace;
            return (trace.Count > 0) ? trace[0] as MutableString : null;
        }

        /// <summary>
        /// Builds backtrace for the exception if it wasn't built yet. 
        /// Captures a full stack trace starting with the current frame and combines it with the trace of the exception.
        /// Called from compiled code.
        /// </summary>
        internal void CaptureExceptionTrace(RubyScope/*!*/ scope) {
            CaptureExceptionTrace(scope, false);
        }

        /// <param name="rescuing">
        /// Called where a compiled frame has caught the exception to match it with its rescue clauses (RubyOps.CanRescue), and no
        /// Ruby code can have kept the exception: the walk of the stack below that frame may be left until it is known to be
        /// needed (see <see cref="TryDeferCatchSite"/>).
        /// </param>
        /// <param name="frameDepth">
        /// The index of the catching frame's record in PrecompiledFrames, if known (-1 otherwise): with <paramref name="rescuing"/>,
        /// the record is told from the others by it rather than by the exception's CLR trace.
        /// </param>
        /// <param name="passed">What <see cref="TakePassedEnsureCapture"/> has returned already, if anything.</param>
        internal void CaptureExceptionTrace(RubyScope/*!*/ scope, bool rescuing, int frameDepth = -1, RubyStackTraceBuilder.CapturedTrace passed = null) {
            // what the ensure clauses the exception went through took of its backtrace (see EnterPassedEnsure): the rest is taken here
            passed = passed ?? TakePassedEnsureCapture();

            if (rescuing && !HasBacktrace && _raiseBacktrace == null && TryDeferCatchSite(scope, frameDepth, passed)) {
                return;
            }
            if (CaptureTrace(scope, passed)) {
                // MRI (setup_exception) only calls #set_backtrace when it has been redefined:
                if (!HasBuiltinSetBacktrace(scope.RubyContext)) {
                    CallRedefinedSetBacktrace(scope.RubyContext);
                }
            }
        }

        /// <summary>
        /// The catch-site walk - the whole CLR stack, every frame's method resolved - is most of what a raise costs, and it grows
        /// with the depth of the stack, while a rescued exception's backtrace is rarely looked at. What it adds to the backtrace
        /// are the frames below the catching frame, and those stay as they are for as long as that frame lives. So a compiled frame
        /// that catches an exception takes everything else now - the exception's own trace, the interpreted frames, the compiled
        /// frames' lines - and leaves the walk until the exception is exposed: its backtrace read, or the exception itself handed to
        /// Ruby code - $!, `rescue => e', a bare raise, the cause of another exception (RubyContext.CurrentException), a `===' of
        /// a rescue clause that is not Module#=== (RubyOps.CompareException) - or thrown on out of the frame (the rethrow in
        /// Body.TransformExceptionHandling). Each of those happens while the frame lives, on its thread, so the walk then taken
        /// from wherever that is has the same frames below it; the frames above it are cut off (<see cref="ResolveCatchSite"/>).
        /// An exception rescued and dropped without any of that is never walked for.
        /// 
        /// Not when the exception might be seen otherwise: a TracePoint, a #set_backtrace of the exception's own (called with the
        /// backtrace right away), or the modes that report CLR frames. And only for a frame whose record in PrecompiledFrames is
        /// the innermost one - the record that identifies it in the walk later.
        ///
        /// That the record is the catching frame's is told by its index, which the frame keeps (<paramref name="frameDepth"/>,
        /// CompiledFrameDepthExpression), or else by the method of the last frame of the exception's CLR trace, which ends with the
        /// catching frame. Reading the trace was most of what is left of the cost of a raise rescued and dropped; with the index it is
        /// read when the walk is taken: the exception is not thrown again before then, so the trace is the same (see CapturedTrace).
        /// </summary>
        /// <param name="passed">What ensure clauses the exception went through took of its backtrace, if any (see EnterPassedEnsure).</param>
        private bool TryDeferCatchSite(RubyScope/*!*/ scope, int frameDepth, RubyStackTraceBuilder.CapturedTrace passed) {
            var context = scope.RubyContext;
            if (scope.InterpretedFrame != null || TracePoint.ActiveEvents != 0 || context.DomainManager.Configuration.DebugMode ||
                context.Options.ExceptionDetail) {
                return false;
            }

            Precompiled.PrecompiledFrames frames;
            int index;
            string name;
            long sequence;
            if (!Precompiled.PrecompiledFrames.TryGetInnermost(out frames, out index, out name, out sequence)) {
                return false;
            }

            StackTrace throwSiteTrace = null;
            if (frameDepth >= 0) {
                if (index != frameDepth) {
                    return false;
                }
            } else {
                // the catching frame is the last of the exception's own trace
                throwSiteTrace = RubyStackTraceBuilder.GetClrStackTrace(_exception, RubyStackTraceBuilder.NeedsClrFileInfo(context));
                int count = throwSiteTrace.FrameCount;
                var catching = (count > 0) ? throwSiteTrace.GetFrame(count - 1)?.GetMethod() : null;
                if (catching == null || !String.Equals(catching.Name, name, StringComparison.Ordinal)) {
                    return false;
                }
            }
            if (!HasBuiltinSetBacktrace(context)) {
                return false;
            }

            _capturedTrace = new RubyStackTraceBuilder.CapturedTrace(context, _exception, throwSiteTrace, null, false, true, passed);
            _pendingCatchSite = new PendingCatchSite(frames, index, name, sequence, Thread.CurrentThread, -1);
            return true;
        }

        // the frames end with one of the method named name (Debug only: the record found for the frame is the frame's)
        private static bool IsLastFrame(StackFrame[]/*!*/ frames, string/*!*/ name) {
            var method = (frames.Length > 0) ? frames[frames.Length - 1]?.GetMethod() : null;
            return method != null && String.Equals(method.Name, name, StringComparison.Ordinal);
        }

        // An exception Kernel#raise made that has gone through a compiled ensure clause before any frame took its backtrace
        // (RubyOps.PassEnsure), in the CLR's first pass; set again on its way through each one (EnterPassedEnsure).
        private bool _passedUntraced;

        /// <summary>
        /// Whether an exception whose backtrace no frame has taken yet may go through a compiled ensure-only body (RubyOps.PassEnsure)
        /// rather than be caught there for its backtrace: an exception Kernel#raise made, which nothing can have kept, that nothing
        /// but Ruby code can see otherwise - no TracePoint, no #set_backtrace of its own, not the modes that report CLR frames.
        /// </summary>
        internal bool CanPassUntraced(RubyScope/*!*/ scope, int frameDepth) {
            // where the frame is in the exception's CLR trace is told by the frames of its name above it, which have to be known
            int above = Precompiled.PrecompiledFrames.CountSameNameAbove(frameDepth);
            if (above < 0) {
                return false;
            }
            if (_passedUntraced) {
                NotePassedFrame(frameDepth, above);
                return true;
            }
            var context = scope.RubyContext;
            if (frameDepth < 0 || HasBacktrace || _raiseBacktrace != null || !RaisedUnkept || scope.InterpretedFrame != null ||
                TracePoint.ActiveEvents != 0 || context.DomainManager.Configuration.DebugMode || context.Options.ExceptionDetail ||
                !HasBuiltinSetBacktrace(context)) {
                return false;
            }
            // An interpreted class or module body that catches the exception takes a backtrace that misses or misorders frames
            // when the frames above it did not catch it (as master does for a body without the ensure clause): the catch here
            // takes it, as it did before.
            for (var interpreted = InterpretedFrame.CurrentFrame.Value; interpreted != null; interpreted = interpreted.Parent) {
                if (RubyStackTraceBuilder.IsModuleBodyName(interpreted.Name)) {
                    return false;
                }
            }
            _passedUntraced = true;
            NotePassedFrame(frameDepth, above);
            return true;
        }

        // For each compiled frame the exception goes through untraced, in this first pass: its record's index and the frames of its
        // name above it in the exception's CLR trace (PrecompiledFrames.CountSameNameAbove), which EnterPassedEnsure cuts the trace by.
        private int[] _passedFrames;
        private int _passedFrameCount;

        private void NotePassedFrame(int frameDepth, int above) {
            if (_passedFrames == null) {
                _passedFrames = new int[8];
            } else if (_passedFrameCount + 2 > _passedFrames.Length) {
                Array.Resize(ref _passedFrames, _passedFrames.Length * 2);
            }
            _passedFrames[_passedFrameCount++] = frameDepth;
            _passedFrames[_passedFrameCount++] = above;
        }

        private int GetPassedFrameAbove(int frameDepth) {
            // the latest note of the frame: an exception thrown on again goes through frames at the same depths in another pass
            for (int i = _passedFrameCount - 2; i >= 0; i -= 2) {
                if (_passedFrames[i] == frameDepth) {
                    return _passedFrames[i + 1];
                }
            }
            return -1;
        }

        /// <summary>
        /// An exception PassEnsure let through without a backtrace is leaving a compiled frame through its ensure clause, in the CLR's
        /// second pass (RubyOps.EnterPassedEnsure, before the clause runs). The clause is Ruby code, which may expose the exception
        /// ($!, a raise whose cause it is): its backtrace is then what a catch here would have taken - the exception's CLR trace up to
        /// this frame, and the frames below it - and it is taken as one caught here would have been: the frame becomes the pending
        /// catch site (see TryDeferCatchSite), until a frame below catches the exception, which takes over (TakePassedEnsureCapture).
        /// 
        /// The exception's CLR trace has the frames of the whole first pass - this frame's callers too, down to the one that catches -
        /// and it is read only when needed, so this frame's place in it is told by the frames of the same name above it, which the
        /// compiled frames' records have. Those, the frames' lines, the interpreted frames and the loops' lines are taken at the first
        /// such ensure clause, before any ensure code runs: the clauses' calls reuse the records of the frames left.
        /// </summary>
        internal void EnterPassedEnsure(RubyScope/*!*/ scope, int frameDepth) {
            if (!_passedUntraced) {
                return;
            }
            var captured = _capturedTrace;
            var pending = _pendingCatchSite;
            if (captured != null && (pending == null || pending.ThrowSiteOccurrence < 0) || _backtrace != null || _raiseBacktrace != null) {
                // the backtrace has been taken - an ensure clause exposed the exception - or set (#set_backtrace, $@=)
                _passedUntraced = false;
                return;
            }

            Precompiled.PrecompiledFrames frames;
            int index;
            string name;
            long sequence;
            if (!Precompiled.PrecompiledFrames.TryGetInnermost(out frames, out index, out name, out sequence) || index != frameDepth) {
                // not reached: the frames above this one have left their records
                Debug.Assert(false, "the frame of a passed ensure clause is not the innermost one");
                return;
            }

            // the CLR frames of this one's name between it and the raise, counted in the first pass (see NotePassedFrame)
            int above = GetPassedFrameAbove(frameDepth);
            if (above < 0) {
                // Not reached: a frame PassEnsure did not note, whose place in the trace is not known. The pending frame stays the one
                // before, if any; the frame below that catches the exception takes the backtrace over.
                Debug.Assert(false, "a passed ensure clause's frame was not noted");
                return;
            }

            if (captured == null) {
                captured = new RubyStackTraceBuilder.CapturedTrace(scope.RubyContext, _exception, null, null, false, true, null);
                _capturedTrace = captured;
            }
            _pendingCatchSite = new PendingCatchSite(frames, index, name, sequence, Thread.CurrentThread, above);
        }

        /// <summary>
        /// What ensure clauses the exception went through without a backtrace took of it (see EnterPassedEnsure), now that a frame
        /// below them has caught the exception and takes its backtrace: the exception's CLR trace has the frames up to that one.
        /// Null if none.
        /// </summary>
        internal RubyStackTraceBuilder.CapturedTrace TakePassedEnsureCapture() {
            var pending = _pendingCatchSite;
            if (pending == null || pending.ThrowSiteOccurrence < 0) {
                return null;
            }
            var captured = _capturedTrace;
            _capturedTrace = null;
            _pendingCatchSite = null;
            _passedUntraced = false;
            return captured;
        }

        private sealed class PendingCatchSite {
            internal readonly Precompiled.PrecompiledFrames/*!*/ Frames;
            internal readonly int Index;
            internal readonly string/*!*/ Name;
            internal readonly long Sequence;
            internal readonly Thread/*!*/ Thread;
            // The frame is one an exception goes through, whose ensure clause runs (EnterPassedEnsure), not the one that caught it:
            // the exception's CLR trace goes on below it, and is cut after the frame of its name that has this many above it. -1 for
            // the frame that caught the exception, with which the trace ends.
            internal readonly int ThrowSiteOccurrence;

            internal PendingCatchSite(Precompiled.PrecompiledFrames/*!*/ frames, int index, string/*!*/ name, long sequence, Thread/*!*/ thread,
                int throwSiteOccurrence) {
                Frames = frames;
                Index = index;
                Name = name;
                Sequence = sequence;
                Thread = thread;
                ThrowSiteOccurrence = throwSiteOccurrence;
            }
        }

        // The catching frame of a captured trace whose catch-site walk is still to be taken (see TryDeferCatchSite).
        private PendingCatchSite _pendingCatchSite;

        /// <summary>
        /// Takes the catch-site walk that <see cref="TryDeferCatchSite"/> left, if any: the exception is about to be exposed to
        /// code that may keep it, or to leave the frame that caught it. Cheap when there is none.
        /// </summary>
        internal static void ResolveCatchSite(Exception exception) {
            if (exception != null) {
                var data = TryGetInstance(exception);
                if (data != null && data._pendingCatchSite != null) {
                    data.ResolveCatchSite();
                }
            }
        }

        internal static bool IsCatchSitePending(Exception/*!*/ exception) {
            var data = TryGetInstance(exception);
            return data != null && data._pendingCatchSite != null;
        }

        private void ResolveCatchSite() {
            var captured = _capturedTrace;
            if (captured == null) {
                _pendingCatchSite = null;
                return;
            }
            lock (captured) {
                var pending = _pendingCatchSite;
                if (pending == null) {
                    return;
                }
                int above = 0;
                bool live = pending.Thread == Thread.CurrentThread && pending.Frames.TryCountLiveAbove(pending.Index, pending.Name, pending.Sequence, out above);

                // The exception has not been thrown again since it was caught: its trace still ends with the catching frame, or goes on
                // from the frame whose ensure clause runs to the one that is to catch it. That frame having been left, the exception
                // exposed before a frame below took it over (not reached), the trace goes on to where it is now: closer than one cut
                // at a frame that is gone.
                if (pending.ThrowSiteOccurrence >= 0 && live) {
                    captured.TakeThrowSiteTrace(pending.Name, pending.ThrowSiteOccurrence);
                } else {
                    captured.TakeThrowSiteTrace();
                }
                _passedUntraced = false;
                Debug.Assert(!live || IsLastFrame(captured.ThrowSiteFrames, pending.Name));
                _pendingCatchSite = null;

                // The catching frame, in a walk from here: the frames of its name the records have above it are those the walk
                // has above it, compiled Ruby frames all having a record (CompiledFrameExpression), so it is the one after them.
                StackFrame[] catchSite = null;
                StackFrame[] frames = null;
                if (live) {
                    frames = RubyStackTraceBuilder.GetClrStackTrace(null, false).GetFrames();
                    for (int i = 0; i < frames.Length; i++) {
                        var method = frames[i].GetMethod();
                        if (method != null && String.Equals(method.Name, pending.Name, StringComparison.Ordinal) && above-- == 0) {
                            catchSite = new StackFrame[frames.Length - i];
                            Array.Copy(frames, i, catchSite, 0, catchSite.Length);
                            break;
                        }
                    }
                }

                if (catchSite != null) {
                    captured.CatchSiteFrames = catchSite;
                } else {
                    // Not reached: only an exception nothing can have kept is deferred, and it is exposed before its frame is left
                    // (the entry's sequence tells that frame from a later one in its slot). Should it be, the frames below are
                    // those of here - closer than none.
                    captured.CatchSiteFrames = frames ?? RubyStackTraceBuilder.GetClrStackTrace(null, false).GetFrames();
                    captured.CatchSiteFramesFromHere = true;
                }
            }
        }

        // Takes the trace of the exception as a Ruby frame first sees it go by; false if it has one already. With what ensure clauses
        // the exception went through took of it (passed, see EnterPassedEnsure).
        private bool CaptureTrace(RubyScope/*!*/ scope, RubyStackTraceBuilder.CapturedTrace passed) {
            if (!HasBacktrace && _raiseBacktrace == null) {
                var context = scope.RubyContext;
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
                _capturedTrace = new RubyStackTraceBuilder.CapturedTrace(context, _exception, null, catchSiteTrace, scope.InterpretedFrame != null,
                    false, passed);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Captures the backtrace of an exception a CLR method has caught, unless a Ruby frame has
        /// already. Compiled Ruby methods leave the capture to whatever handles the exception (see
        /// MethodDefinition.TransformBody), and when that is C# rather than Ruby - a thread's start
        /// routine, a library that parks the exception to throw it again later, the host reporting it -
        /// the Ruby frames it passed are in the exception's own CLR trace, which a `throw' of the
        /// exception starts over. Call it from the catch block. Does nothing for an exception that was
        /// never thrown.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CaptureExceptionTrace(RubyContext/*!*/ context) {
            var passed = TakePassedEnsureCapture();
            if (HasBacktrace || _raiseBacktrace != null) {
                return;
            }
            // the catch site's callers: this walk has the catching method too, below which they are (see Build)
            var captured = new RubyStackTraceBuilder.CapturedTrace(context, _exception,
                RubyStackTraceBuilder.GetClrStackTrace(null, context.DomainManager.Configuration.DebugMode), passed);
            if (captured.ThrowSiteTrace.FrameCount == 0) {
                return;
            }
            _capturedTrace = captured;
            if (!HasBuiltinSetBacktrace(context)) {
                CallRedefinedSetBacktrace(context);
            }
        }

        /// <summary>
        /// MRI's setup_exception with a #set_backtrace of the exception's own: the method is handed the
        /// backtrace of the raise and #backtrace is whatever it leaves - the raise's if it calls super,
        /// nil if it does not - while #backtrace_locations is the raise's either way (see
        /// RaiseBacktrace). An exception the method raises is dropped: the exception being raised
        /// is still the one raised, as in MRI. Without this the method's error escaped the rescue
        /// around the raise - the capture runs in a handler the DLR emits as a catch and a rethrow,
        /// and from a C# catch block (a thread's start) it escaped the thread. Control flow still
        /// goes through: a break or a throw out of the method, a thread being killed or interrupted.
        /// Called once per exception: the frames the exception goes by later find RaiseBacktrace set.
        /// </summary>
        private void CallRedefinedSetBacktrace(RubyContext/*!*/ context) {
            RubyArray raised = Backtrace ?? new RubyArray();
            _raiseBacktrace = raised;
            Backtrace = null;
            try {
                DynamicSetBacktrace(context, raised);
            } catch (Exception e) when (!(e is StackUnwinder || e is ThreadInterruptedException)) {
                // MRI drops it too; #backtrace stays as the method left it
            }
        }

        /// <summary>
        /// The backtrace of the raise when #set_backtrace was redefined and handed it, whatever the
        /// method did with it; null otherwise. Exception#backtrace_locations falls back to it.
        /// </summary>
        public RubyArray RaiseBacktrace {
            get { return _raiseBacktrace; }
        }

        /// <summary>
        /// Kernel#raise of an exception that has no backtrace: it gets one from where it is raised now,
        /// and a redefined #set_backtrace is called again, as in MRI.
        /// </summary>
        public void ResetRaiseBacktrace() {
            _raiseBacktrace = null;
        }

        /// <summary>
        /// Whether #set_backtrace of the exception is IronRuby's own: the library method, or the
        /// prelude's (Src/StdLib/ironruby/ruby4.rb) which keeps Thread::Backtrace::Locations around
        /// and passes strings on to the library method. Called with the backtrace the runtime has just
        /// captured, the prelude's would only parse the strings into Locations eagerly - which its
        /// #backtrace_locations does on demand all the same.
        /// </summary>
        private bool HasBuiltinSetBacktrace(RubyContext/*!*/ context) {
            return HasBuiltinSetBacktrace(context, _exception);
        }

        /// <summary>
        /// Whether CanRescue's capture of the exception's backtrace runs no Ruby code: it has one already, or the #set_backtrace the
        /// capture would call is IronRuby's own (see RubyOps.RescuePassMayTest).
        /// </summary>
        internal static bool CaptureRunsNoRubyCode(RubyContext/*!*/ context, Exception/*!*/ exception) {
            var data = TryGetInstance(exception);
            return data != null && (data.HasBacktrace || data._raiseBacktrace != null) || HasBuiltinSetBacktrace(context, exception);
        }

        private static bool HasBuiltinSetBacktrace(RubyContext/*!*/ context, Exception/*!*/ exception) {
            var method = context.GetImmediateClassOf(exception).ResolveMethod("set_backtrace", VisibilityContext.AllVisible).Info;
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
            var result = new RubyExceptionData(e);
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
                    var rubyObject = _exception as IRubyObject;
                    if (rubyObject != null) {
                        var immediateClass = rubyObject.ImmediateClass;
                        if (immediateClass != null) {
                            return _message = GetDefaultMessage(immediateClass.NominalClass);
                        }
                    }
                    string clrMessage = _exception.Message;
                    if (_exception is DivideByZeroException && clrMessage == _DefaultDivideByZeroMessage) {
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
                if (_capturedTrace != null) {
                    if (_pendingCatchSite != null) {
                        ResolveCatchSite();
                    }
                    FormatCapturedTrace();
                }
                return _backtrace;
            }
            set {
                _capturedTrace = null;
                _pendingCatchSite = null;
                _passedUntraced = false;
                _backtrace = value;
            }
        }

        /// <summary>
        /// Whether the exception has a backtrace, without formatting a captured one.
        /// </summary>
        public bool HasBacktrace {
            get {
                return _backtrace != null || _capturedTrace != null;
            }
        }

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
                    if (_calleeLabel != null && _backtrace != null) {
                        _backtrace.Insert(0, RubyStackTraceBuilder.FormatCalleeFrame(_backtrace, _calleeFile, _calleeLine, _calleeLabel));
                    }
                }
            }
        }

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

        internal void SetCalleeFrame(string file, int line, string/*!*/ label) {
            _calleeFile = file;
            _calleeLine = line;
            _calleeLabel = label;
        }

        /// <summary>
        /// NameError#local_variables: the local variables of the scope the error was made in, which
        /// MRI takes from the instruction sequence it keeps on the error. Null when there is none.
        /// The names rather than the scope are kept, so that the error does not keep the variables'
        /// values alive.
        /// </summary>
        public RubyArray LocalVariables {
            get { return _localVariables; }
            set { _localVariables = value; }
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
            _cause = (cause == _exception) ? null : cause;
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

        internal static Exception/*!*/ HandleException(RubyScope/*!*/ scope, Exception/*!*/ exception) {
            var context = scope.RubyContext;
            // already handled:
            var instanceData = GetInstance(exception);
            if (instanceData.Handled) {
                return exception;
            }

            RubyClass exceptionClass = context.GetClass(exception.GetType());

            // the built-in #new and #initialize (IsHandledInPlace, the rescue filter, relies on this being the test):
            if (IsBuiltinConstruction(context, exceptionClass)) {
                instanceData.Handled = true;
                return exception;
            }

            object message = instanceData.Message;
            // Errno classes extend CLR exceptions of several roots (ExternalException,
            // IOException, ...), so SystemCallError membership is decided on the Ruby side.
            if (exceptionClass.IsSubclassOf(context.GetClass(typeof(System.Runtime.InteropServices.ExternalException)))) {
                message = StripSystemCallErrorPrefix(exceptionClass, message);
            }

            // The new exception is never thrown and has no trace of its own: it gets the one the thrown exception has come by
            // so far (an Errno raised by a library method had none at all when rescued). Taken before "new" runs Ruby code,
            // which would reuse the PrecompiledFrames slots of the compiled frames the exception left (and their lines).
            bool captured = instanceData.CaptureTrace(scope, instanceData.TakePassedEnsureCapture());

            var site = exceptionClass.NewSite;
            Exception newException;
            try {
                newException = site.Target(site, exceptionClass, message) as Exception;
            } catch (Exception e) {
                // MRI: this can lead to stack overflow:
                return HandleException(scope, e);
            }

            // MRI doesn't handle this correctly, see http://redmine.ruby-lang.org/issues/show/1886:
            if (newException == null) {
                newException = RubyExceptions.CreateTypeError("exception object expected");
            }

            var newInstanceData = GetInstance(newException);
            
            newInstanceData.Handled = true;
            // a cause decided for the thrown exception (by a filter it went through: RubyOps.PassEnsure, RescuePassCommit,
            // SetCauseLeavingEnsure) is the cause of the one Ruby code sees
            if (instanceData._hasCause && !newInstanceData._hasCause) {
                newInstanceData.SetCause(instanceData._cause);
            }
            newInstanceData._backtrace = instanceData._backtrace;
            newInstanceData._capturedTrace = instanceData._capturedTrace;
            newInstanceData._pendingCatchSite = instanceData._pendingCatchSite;
            if (captured && !newInstanceData.HasBuiltinSetBacktrace(context)) {
                newInstanceData.CallRedefinedSetBacktrace(context);
            }
            return newException;
        }

        /// <summary>
        /// A SystemCallError thrown from C# already says "&lt;strerror&gt; - &lt;detail&gt;"
        /// (RubyExceptions.MakeMessage). The Ruby-level initialize it is about to be handed to -
        /// the prelude's SystemCallError#initialize - builds that same form from the detail, so it
        /// gets the detail alone, or the error would name itself twice: "No such file or directory
        /// - No such file or directory - path". The class's strerror text is its hidden @__message__ (see the prelude's Errno).
        /// </summary>
        private static object StripSystemCallErrorPrefix(RubyClass/*!*/ exceptionClass, object message) {
            var text = message as MutableString;
            if (text == null) {
                return message;
            }
            for (RubyClass c = exceptionClass; c != null; c = c.SuperClass) {
                object constant;
                if (c.Context.TryGetInstanceVariable(c, RubyUtils.GetCoreStateInstanceVariableName("@__message__"), out constant)) {
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
    }
}

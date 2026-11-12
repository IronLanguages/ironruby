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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using IronRuby.Builtins;
using Microsoft.Scripting;
using Microsoft.Scripting.Interpreter;

namespace IronRuby.Runtime {
    public sealed class RubyStackTraceBuilder {
        private readonly RubyArray/*!*/ _trace;
        private readonly bool _needsClrFileInfo;
        private readonly bool _exceptionDetail;
        private readonly RubyEncoding/*!*/ _encoding;
        private IList<InterpretedFrameInfo> _interpretedFrames;
        private int _interpretedFrameIndex;
        // The lines of the compiled frames (see CompiledFrameExpression), outermost first; a compiled frame of the trace is
        // matched with the innermost one of its name not matched yet.
        private Precompiled.PrecompiledFrames.Frame[] _compiledFrames;
        private int _compiledFrameIndex;
        // The lines of the interpreted frames that run a loop the interpreter has compiled, by depth (see PrecompiledFrames.SetLoopLine).
        private Precompiled.PrecompiledFrames.LoopLineInfo[] _loopLines;
        // An exception's trace: a compiled frame whose ensure clause raised may be there twice (see IsFuncletParent).
        private bool _isExceptionTrace;
        // The CLR frame the walk last went by, and whether the compiled frame record matched with it was in an ensure clause.
        private MethodBase _previousMethod;
        private bool _previousInEnsure;
        // Frames reported with the source info of the next frame out that has its own: a library
        // method's, and a core method written in Ruby (see IsInternalFile). Innermost first.
        private readonly List<string>/*!*/ _deferredMethodNames = new List<string>();
        // How many of the last deferred frames are library methods' (the methods a core method written in Ruby has called).
        private int _libraryRun;
        // Whether a call site has been seen since the library frame last deferred: it called the method that is still on top
        // as a Ruby method.
        private bool _callSiteSinceLastLibrary;
        private readonly RubyContext/*!*/ _context;
        // The number of frames of the trace up to and including the outermost one that is not a host's CLR frame, reported or
        // skipped (see RubyTrace); -1 while there is none.
        private int _rubyFrameEnd = -1;
        // The frame TryGetStackFrameInfo has just read is a CLR method of no Ruby library (a "visible CLR frame").
        private bool _isClrFrame;
        // TracePoint's c_call location needs the frame that really made the call, which inside a
        // core method written in Ruby is that method's own, not its caller's.
        private bool _keepInternalFrames;
        // The walk stops once this many frames are reported and known to be followed by nothing a
        // host would cut off (see RubyTrace); Thread.new needs only the one frame that called it.
        private int _maxFrames = Int32.MaxValue;

        private RubyStackTraceBuilder(RubyContext/*!*/ context)
            : this(context, context.GetPathEncoding()) {
        }

        private RubyStackTraceBuilder(RubyContext/*!*/ context, RubyEncoding/*!*/ encoding) {
            _context = context;
            _needsClrFileInfo = NeedsClrFileInfo(context);
            _exceptionDetail = context.Options.ExceptionDetail;
            _encoding = encoding;
            _trace = new RubyArray();
        }

        internal RubyStackTraceBuilder(RubyContext/*!*/ context, Exception/*!*/ exception, StackTrace catchSiteTrace, bool isCatchSiteInterpreted)
            : this(context) {
            // Compiled trace: contains frames starting with the throw site up to the first filter/catch that the exception was caught by:
            StackTrace throwSiteTrace = GetClrStackTrace(exception, _needsClrFileInfo);
            _interpretedFrames = GetInterpretedFrames(exception);
            SetCompiledFrames(Precompiled.PrecompiledFrames.CaptureLines(true));
            _loopLines = Precompiled.PrecompiledFrames.CaptureLoopLines();
            _isExceptionTrace = true;
            AddExceptionBacktrace(throwSiteTrace, catchSiteTrace, isCatchSiteInterpreted);
        }

        private RubyStackTraceBuilder(CapturedTrace/*!*/ captured)
            : this(captured.Context, captured.Encoding) {
            _interpretedFrames = captured.InterpretedFrames;
            SetCompiledFrames(captured.CompiledFrames);
            _loopLines = captured.LoopLines;
            _isExceptionTrace = true;
            if (captured.IsCatchSiteClr) {
                AddClrCaughtBacktrace(captured.ThrowSiteTrace, captured.CatchSiteTrace);
            } else if (captured.CatchSiteFrames != null) {
                AddBacktrace(captured.ThrowSiteTrace.GetFrames(), 0, false);
                AddBacktrace(captured.CatchSiteFrames, 0, false, !captured.CatchSiteFramesFromHere);
            } else {
                AddExceptionBacktrace(captured.ThrowSiteTrace, captured.CatchSiteTrace, captured.IsCatchSiteInterpreted);
            }
        }

        // The catch site is a CLR method (RubyExceptionData.CaptureExceptionTrace(RubyContext)): the throw-site trace ends with
        // it, and the catch-site walk has it - below the frames of the capture itself - with its callers below it. They go on
        // from where the throw-site trace ends.
        private void AddClrCaughtBacktrace(StackTrace/*!*/ throwSiteTrace, StackTrace/*!*/ catchSiteTrace) {
            var throwFrames = throwSiteTrace.GetFrames();
            AddBacktrace(throwFrames, 0, false);

            var catchingMethod = throwFrames[throwFrames.Length - 1].GetMethod();
            var catchFrames = catchSiteTrace.GetFrames();
            for (int i = 0; i < catchFrames.Length; i++) {
                if (catchingMethod != null && catchFrames[i].GetMethod() == catchingMethod) {
                    AddBacktrace(new ArraySegment<StackFrame>(catchFrames, i + 1, catchFrames.Length - i - 1), 0, false);
                    return;
                }
            }
        }

        /// <summary>
        /// False where a CLR stack walk sees no frames at all: .NET on WebAssembly with the assemblies
        /// AOT-compiled (wasm code carries no unwind information). Backtraces are then made of the
        /// interpreter's own frame chain alone, so frames of compiled code (the method JIT, lambdas the
        /// interpreter compiled) and of library methods are missing from them.
        /// IR_INTERPRETED_BACKTRACES=1 asks for the same anywhere, to test it.
        /// </summary>
        internal static readonly bool ClrStackAvailable =
            new StackTrace(false).FrameCount > 0 && Environment.GetEnvironmentVariable("IR_INTERPRETED_BACKTRACES") != "1";

        private static IList<InterpretedFrameInfo> CurrentInterpretedFrames() {
            var frame = InterpretedFrame.CurrentFrame.Value;
            return frame != null ? new List<InterpretedFrameInfo>(frame.GetStackTraceDebugInfo()) : null;
        }

        // The interpreted-frames-only backtrace (see ClrStackAvailable), innermost first.
        private void AddInterpretedBacktrace(IList<InterpretedFrameInfo> frames, int skipFrames) {
            if (frames == null) {
                return;
            }
            foreach (var info in frames) {
                string methodName = info.MethodName;
                string file = info.DebugInfo != null ? info.DebugInfo.FileName : null;
                int line = info.DebugInfo != null ? info.DebugInfo.StartLine : 0;
                if (methodName == InterpretedCallSiteName || !TryParseRubyMethodName(ref methodName, ref file, ref line)) {
                    continue;
                }
                if (!_keepInternalFrames && IsInternalFile(file)) {
                    if (IsInternalMethodFrame(methodName)) {
                        _deferredMethodNames.Add(methodName);
                    }
                    continue;
                }
                foreach (var deferred in _deferredMethodNames) {
                    if (skipFrames == 0) {
                        _trace.Add(MutableString.Create(FormatFrame(file, line, deferred), _encoding));
                    } else {
                        skipFrames--;
                    }
                }
                _deferredMethodNames.Clear();
                if (skipFrames == 0) {
                    _trace.Add(MutableString.Create(FormatFrame(file, line, methodName), _encoding));
                } else {
                    skipFrames--;
                }
            }
        }

        private void AddExceptionBacktrace(StackTrace/*!*/ throwSiteTrace, StackTrace catchSiteTrace, bool isCatchSiteInterpreted) {
            if (!ClrStackAvailable) {
                AddInterpretedBacktrace(_interpretedFrames, 0);
                return;
            }
            var throwFrames = throwSiteTrace.GetFrames();
            AddBacktrace(throwFrames, 0, false);

            // A loop the interpreter has compiled (CompiledLoopInstruction) catches in its own DynamicMethod, which the
            // interpreted frame's Run method called: the throw-site trace ends with that method, not with Run, and the Run frame
            // below it, which the catch-site walk has next, is the catching frame's own - not one already matched. Skipping it
            // gave the frame's line to the frame below and lost the outermost one.
            if (isCatchSiteInterpreted && throwFrames.Length > 0 && !IsInterpreterRunFrame(throwFrames[throwFrames.Length - 1])) {
                if (catchSiteTrace != null) {
                    AddBacktrace(catchSiteTrace.GetFrames(), 0, false, false);
                }
                return;
            }

            // Compiled trace: contains frames above and including the first Ruby filter/catch site that the exception was caught by:
            if (catchSiteTrace != null) {
                // Interpreted: skip one interpreter Run method frame - is was already matched with the last processed interpreted frame:
                // Compiled: skip the catch-site frame - the throw-site trace ends with it
                AddBacktrace(catchSiteTrace.GetFrames(), 0, isCatchSiteInterpreted, !isCatchSiteInterpreted);
            }
        }

        /// <summary>
        /// What the backtrace of a raised exception is made of, taken where the first Ruby frame saw
        /// the exception go by - the stack walks, and the interpreted frames the interpreter saved on
        /// the exception - but not yet turned into strings. Most exceptions are rescued and dropped
        /// without anyone asking for their backtrace, and formatting it (resolving every frame's
        /// method, parsing the encoded names, building a string per frame) is most of what a raise
        /// used to cost. <see cref="Build"/> does it on first use and yields exactly what building it
        /// eagerly yielded: everything it reads is either captured here or fixed for the context.
        /// </summary>
        internal sealed class CapturedTrace {
            internal readonly RubyContext/*!*/ Context;
            internal readonly RubyEncoding/*!*/ Encoding;
            internal readonly StackTrace/*!*/ ThrowSiteTrace;
            internal readonly StackTrace CatchSiteTrace;
            internal readonly IList<InterpretedFrameInfo> InterpretedFrames;
            internal readonly Precompiled.PrecompiledFrames.Frame[] CompiledFrames;
            internal readonly Precompiled.PrecompiledFrames.LoopLineInfo[] LoopLines;
            internal readonly bool IsCatchSiteInterpreted;
            internal readonly bool IsCatchSiteClr;

            // The catch-site walk from the catching frame down, when it was taken after the catch (RubyExceptionData.TryDeferCatchSite):
            // the catching frame first, which is skipped as CatchSiteTrace's is. Set, under a lock of this object, before Build.
            internal StackFrame[] CatchSiteFrames;
            // CatchSiteFrames is a walk from where the catching frame could not be found, which does not start with it
            internal bool CatchSiteFramesFromHere;

            internal CapturedTrace(RubyContext/*!*/ context, Exception/*!*/ exception, StackTrace catchSiteTrace, bool isCatchSiteInterpreted)
                : this(context, exception, null, catchSiteTrace, isCatchSiteInterpreted) {
            }

            internal CapturedTrace(RubyContext/*!*/ context, Exception/*!*/ exception, StackTrace throwSiteTrace, StackTrace catchSiteTrace,
                bool isCatchSiteInterpreted) {
                Context = context;
                Encoding = context.GetPathEncoding();
                // the exception's own trace grows as it is rethrown, and a raise of it starts it over:
                ThrowSiteTrace = throwSiteTrace ?? GetClrStackTrace(exception, NeedsClrFileInfo(context));
                // the interpreter's frames are saved on the exception once, and raise clears them:
                InterpretedFrames = GetInterpretedFrames(exception);
                // the compiled frames' lines change as they run on, and the frames it has left are reused by the next call:
                CompiledFrames = Precompiled.PrecompiledFrames.CaptureLines(true);
                LoopLines = Precompiled.PrecompiledFrames.CaptureLoopLines();
                CatchSiteTrace = catchSiteTrace;
                IsCatchSiteInterpreted = isCatchSiteInterpreted;
            }

            internal CapturedTrace(RubyContext/*!*/ context, Exception/*!*/ exception, StackTrace/*!*/ clrCatchSiteTrace)
                : this(context, exception, clrCatchSiteTrace, false) {
                IsCatchSiteClr = true;
            }

            internal RubyArray/*!*/ Build() {
                return new RubyStackTraceBuilder(this).RubyTrace;
            }
        }

        /// <summary>
        /// The interpreted frames of the exception's trace: those the interpreter saved on it when it left an interpreted frame
        /// (Interpreter.HandleException) - or, if it has left none yet, the thread's interpreted frames, every one of them still on
        /// the stack. The trace is taken where the first Ruby frame sees the exception go by (RubyExceptionData.CaptureExceptionTrace):
        /// an exception filter, which compiled code runs while the stack is searched for a handler, before any frame has been left.
        /// An exception that a compiled rule or library method throws into a compiled Ruby method is seen by that method's filter
        /// before any interpreted frame - its callers' - has saved anything; without them every interpreted frame in the trace, and
        /// the library frames reported with an interpreted frame's line, went missing (the backtrace ended at the compiled frames).
        /// </summary>
        private static IList<InterpretedFrameInfo> GetInterpretedFrames(Exception/*!*/ exception) {
            var saved = InterpretedFrame.GetExceptionStackTrace(exception);
            if (saved != null) {
                return saved;
            }
            var current = InterpretedFrame.CurrentFrame.Value;
            return (current != null) ? new List<InterpretedFrameInfo>(current.GetStackTraceDebugInfo()) : null;
        }

        internal RubyStackTraceBuilder(RubyContext/*!*/ context, int skipFrames)
            : this(context, skipFrames, false) {
        }

        internal RubyStackTraceBuilder(RubyContext/*!*/ context, int skipFrames, bool keepInternalFrames)
            : this(context, skipFrames, keepInternalFrames, Int32.MaxValue) {
        }

        internal RubyStackTraceBuilder(RubyContext/*!*/ context, int skipFrames, bool keepInternalFrames, int maxFrames)
            : this(context) {
            _keepInternalFrames = keepInternalFrames;
            _maxFrames = maxFrames;

            // Kernel#caller and #caller_locations: -X:ExceptionDetail adds the CLR frames to exception
            // backtraces, not here, where code reads the frames - require_relative takes its directory
            // from the first one, and would get a .cs file of the runtime instead of the caller's.
            _exceptionDetail = false;

            if (!ClrStackAvailable) {
                AddInterpretedBacktrace(CurrentInterpretedFrames(), skipFrames);
                return;
            }
            var trace = GetClrStackTrace(null, _needsClrFileInfo);

            _interpretedFrames = InterpretedFrame.CurrentFrame.Value != null ?
                new List<InterpretedFrameInfo>(InterpretedFrame.CurrentFrame.Value.GetStackTraceDebugInfo()) :
                null;
            SetCompiledFrames(Precompiled.PrecompiledFrames.CaptureLines(false));
            _loopLines = Precompiled.PrecompiledFrames.CaptureLoopLines();

            AddBacktrace(trace.GetFrames(), skipFrames, false);
        }

        /// <summary>
        /// Builds a backtrace for a thread other than the caller's from that thread's interpreted frame
        /// chain, which the interpreter maintains as a linked list hanging off a thread-local and which
        /// any thread may read.  A CLR stack walk is not an option here - .NET Core has no
        /// StackTrace(Thread) and no Thread.Suspend - so this is what there is, and it costs nothing
        /// when nobody asks: the chain is already there for the owning thread's own backtraces.
        ///
        /// The startup library's methods and blocks that run a block are compiled code, which the chain does not
        /// have; they keep frames of their own (<see cref="Precompiled.PrecompiledFrames"/>), each with the interpreted
        /// frame it was entered from, which puts it among the interpreted ones: above that frame, and below the
        /// interpreted frames entered since.
        ///
        /// The thread is running while we read, so the result is a snapshot rather than an instant.
        /// Frames are not recycled, so a frame that was left while we were walking still points at its
        /// old parent and the walk terminates; the depth cap is there for the case where it somehow
        /// does not.
        /// </summary>
        internal RubyStackTraceBuilder(RubyContext/*!*/ context, InterpretedFrame frame, Precompiled.PrecompiledFrames.Frame[]/*!*/ precompiled,
            Precompiled.PrecompiledFrames.LoopLineInfo[] loopLines, int maxFrames = Int32.MaxValue)
            : this(context) {

            // the depth of each frame in the chain, which the loop lines are kept by
            int frameDepth = -1;
            for (var f = frame; f != null && frameDepth < MaxThreadBacktraceDepth; f = f.Parent) {
                frameDepth++;
            }

            int next = 0;
            for (int depth = 0; (frame != null || next < precompiled.Length) && depth < MaxThreadBacktraceDepth && _trace.Count < maxFrames; depth++) {
                string methodName;
                string file;
                int line;

                // a precompiled frame entered from this interpreted frame is above it (from none: below all of them, or
                // its interpreted frame was left while we read)
                if (next < precompiled.Length && (frame == null || precompiled[next].Below == frame)) {
                    var compiled = precompiled[next++];
                    methodName = compiled.Name;
                    file = null;
                    line = compiled.Line;
                    if (!TryParseRubyMethodName(ref methodName, ref file, ref line)) {
                        continue;
                    }
                    Precompiled.PrecompiledCode.RebaseFrameFile(ref file);
                } else {
                    methodName = frame.Name;
                    var debugInfo = frame.GetDebugInfo(frame.InstructionIndex);
                    if (debugInfo != null) {
                        file = debugInfo.FileName;
                        line = GetLine(debugInfo, loopLines, frameDepth);
                    } else {
                        file = null;
                        line = 0;
                    }
                    frame = frame.Parent;
                    frameDepth--;

                    if (methodName == InterpretedCallSiteName) {
                        continue;
                    }

                    // Frames the interpreter runs on behalf of the DLR rather than of Ruby have no encoded
                    // Ruby name; they are not part of a Ruby backtrace and MRI has nothing to put in their
                    // place, so leave them out.
                    if (!TryParseRubyMethodName(ref methodName, ref file, ref line)) {
                        continue;
                    }
                }

                if (IsInternalFrameFile(file)) {
                    AddInternalFrame(methodName);
                    continue;
                }

                foreach (var deferred in _deferredMethodNames) {
                    _trace.Add(CreateFrame(FormatFrame(file, line, deferred)));
                }
                _deferredMethodNames.Clear();

                _trace.Add(CreateFrame(FormatFrame(file, line, methodName)));
            }
        }

        private const int MaxThreadBacktraceDepth = 10000;

        /// <summary>
        /// The line of an interpreted frame at <paramref name="depth"/> in its chain: its sequence point's, or - when that is the
        /// cleared one a loop starts with, and the frame runs the loop compiled - the line of the loop's statement it is at.
        /// </summary>
        private static int GetLine(DebugInfo/*!*/ debugInfo, Precompiled.PrecompiledFrames.LoopLineInfo[] loopLines, int depth) {
            int line = debugInfo.StartLine;
            return debugInfo.IsClear ? Precompiled.PrecompiledFrames.GetLoopLine(loopLines, depth, line) : line;
        }

        // A frame in the path encoding. Under the C locale (US-ASCII) a frame with a non-ASCII file or method name is its
        // UTF-8 bytes in a BINARY string, as MRI's is and as the path layer hands out such a path (RubyContext.EncodePath):
        // a US-ASCII string of those characters was invalid, and require_relative, caller_locations(0)[0].path and the
        // error report raised "invalid byte sequence in US-ASCII".
        private MutableString/*!*/ CreateFrame(string/*!*/ frame) {
            return RubyContext.TryEncodeRawAsciiPath(frame, _encoding) ?? MutableString.Create(frame, _encoding);
        }

        private void SetCompiledFrames(Precompiled.PrecompiledFrames.Frame[] frames) {
            _compiledFrames = frames;
            _compiledFrameIndex = (frames != null) ? frames.Length - 1 : -1;
        }

        /// <summary>
        /// The line of a frame of a compiled lambda: of the innermost compiled frame of that name not matched yet, which
        /// is the next one out, or 0 if there is none.
        /// </summary>
        private int TakeCompiledFrameLine(string/*!*/ encodedName) {
            for (int i = _compiledFrameIndex; i >= 0; i--) {
                if (String.Equals(_compiledFrames[i].Name, encodedName, StringComparison.Ordinal)) {
                    _compiledFrameIndex = i - 1;
                    _previousInEnsure = _compiledFrames[i].InEnsure;
                    return _compiledFrames[i].Line;
                }
            }
            return 0;
        }

        /// <summary>
        /// The file and line of the innermost Ruby frame, taken from the interpreter's own frame
        /// chain rather than from a CLR stack walk. This is what backtrace[0] reports, but without
        /// the cost of `new StackTrace(true)` (~1ms: it reads the PDBs of every frame on the CLR
        /// stack), which matters for the callers that only want a location - Module#const_set and
        /// Module#autoload, which the core library runs hundreds of times at startup.
        ///
        /// Returns false when there is no interpreted frame to read, in which case the caller falls
        /// back to the stack walk.
        /// </summary>
        internal static bool TryGetInterpretedFrameLocation(out string file, out int line) {
            file = null;
            line = 0;
            var frame = InterpretedFrame.CurrentFrame.Value;
            var loopLines = Precompiled.PrecompiledFrames.CaptureLoopLines();
            int frameDepth = -1;
            if (loopLines != null) {
                for (var f = frame; f != null && frameDepth < MaxThreadBacktraceDepth; f = f.Parent) {
                    frameDepth++;
                }
            }
            for (int depth = 0; frame != null && depth < MaxThreadBacktraceDepth; depth++, frameDepth--, frame = frame.Parent) {
                string methodName = frame.Name;
                if (methodName == InterpretedCallSiteName) {
                    continue;
                }

                string frameFile;
                int frameLine;
                var debugInfo = frame.GetDebugInfo(frame.InstructionIndex);
                if (debugInfo != null) {
                    frameFile = debugInfo.FileName;
                    frameLine = GetLine(debugInfo, loopLines, frameDepth);
                } else {
                    frameFile = null;
                    frameLine = 0;
                }

                if (!TryParseRubyMethodName(ref methodName, ref frameFile, ref frameLine)) {
                    continue;
                }

                // A frame of the Ruby half of the core library is reported at its caller's location,
                // so it is not the answer either - keep walking out.
                if (IsInternalFrameFile(frameFile)) {
                    continue;
                }

                if (frameFile == null) {
                    return false;
                }

                file = frameFile;
                line = frameLine;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a CLR stack walk has to read file and line info out of the PDBs. Only a debug-mode
        /// run has any use for it: a Ruby frame carries its own file and line (in the interpreter's
        /// debug info, or encoded in the method name), and the frames that would answer from a PDB are
        /// IronRuby's own, which never reach the Ruby backtrace. Asking for it costs on the order of a
        /// millisecond per stack walk - i.e. per raise - since the CLR then loads and reads the PDB of
        /// every assembly on the stack.
        /// </summary>
        internal static bool NeedsClrFileInfo(RubyContext/*!*/ context) {
            // only the modes that report CLR frames pay for symbol resolution
            return context.Options.ExceptionDetail || context.DomainManager.Configuration.DebugMode;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // CF
        internal static StackTrace GetClrStackTrace(Exception exception, bool needFileInfo) {
            return exception != null ? new StackTrace(exception, needFileInfo) : new StackTrace(needFileInfo);
        }

        /// <summary>
        /// Whether a catch-site walk taken in an interpreted frame (see RubyExceptionData.CaptureExceptionTrace)
        /// can stand for the walk of any other exception the frame catches: whether it shows the frame
        /// once. While the interpreter runs a frame's finally blocks on the way out of an exception it
        /// re-enters the frame's Run method from HandleException, and the walk then has two runs of it.
        /// </summary>
        internal static bool IsReusableCatchSiteTrace(StackTrace/*!*/ trace) {
            var frames = trace.GetFrames();
            int i = 0;
            while (i < frames.Length && !IsInterpreterRunFrame(frames[i])) {
                i++;
            }
            if (i == frames.Length) {
                return false;
            }
            var interpreterType = frames[i].GetMethod().DeclaringType;
            while (i < frames.Length && IsInterpreterRunFrame(frames[i])) {
                i++;
            }
            if (i == frames.Length) {
                return false;
            }
            var next = frames[i].GetMethod();
            return next != null && !(next.DeclaringType == interpreterType && next.Name == "HandleException");
        }

        private static bool IsInterpreterRunFrame(StackFrame/*!*/ frame) {
            var method = frame.GetMethod();
            return method != null && InterpretedFrame.IsInterpretedFrame(method);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // CF
        private static string GetFileName(StackFrame/*!*/ frame) {
            return frame.GetFileName();
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // CF
        private static string/*!*/ GetAssemblyName(Assembly/*!*/ assembly) {
            return assembly.GetName().Name;
        }

        /// <summary>
        /// The trace, without the CLR frames below the outermost Ruby frame: the frames of the host that ran the Ruby code
        /// (ScriptEngine.Execute's caller and its callers, issue 163). CRuby has nothing below the main script's frame, and
        /// `ir' never showed any - its own frames are IronRuby's, which are never reported. A CLR method between Ruby frames,
        /// one that called back into Ruby, is still reported, and -X:ExceptionDetail keeps every frame.
        /// </summary>
        public RubyArray/*!*/ RubyTrace {
            get {
                if (!_exceptionDetail && _rubyFrameEnd >= 0 && _rubyFrameEnd < _trace.Count) {
                    _trace.RemoveRange(_rubyFrameEnd, _trace.Count - _rubyFrameEnd);
                }
                // a library frame flushed with the frame that ended the walk can take it past the limit
                if (_trace.Count > _maxFrames) {
                    _trace.RemoveRange(_maxFrames, _trace.Count - _maxFrames);
                }
                return _trace;
            }
        }

        private const int NextFrameLine = Int32.MinValue;
        internal const string InterpretedCallSiteName = "CallSite.Target";

        private void AddBacktrace(IEnumerable<StackFrame> stackTrace, int skipFrames, bool skipInterpreterRunMethod) {
            AddBacktrace(stackTrace, skipFrames, skipInterpreterRunMethod, false);
        }

        // skipCompiledCatchSite: the first compiled Ruby frame is the catch site, which the throw-site trace has already gone
        // through. It is skipped as a frame, whether or not it was reported: skipping the first frame reported instead lost
        // the frame above a catch site that is not reported, such as a block of the Ruby half of the core library (the
        // `require' of gem_compat.rb catching a LoadError dropped the frame of the code calling require, once that block was
        // compiled - from the start with the precompiled startup library).
        private void AddBacktrace(IEnumerable<StackFrame> stackTrace, int skipFrames, bool skipInterpreterRunMethod, bool skipCompiledCatchSite) {
            bool skippedAny = false;
            if (stackTrace != null) {
                foreach (var frame in InterpretedFrame.GroupStackFrames(stackTrace)) {
                    string methodName, file;
                    int line;
                    bool isClrFrame = false;

                    MethodBase previousMethod = _previousMethod;
                    bool previousInEnsure = _previousInEnsure;
                    _previousMethod = frame.GetMethod();
                    _previousInEnsure = false;
                    if (_isExceptionTrace && previousInEnsure && IsFuncletParent(previousMethod, _previousMethod)) {
                        continue;
                    }

                    if (_interpretedFrames != null && _interpretedFrameIndex < _interpretedFrames.Count
                        && InterpretedFrame.IsInterpretedFrame(frame.GetMethod())) {

                        if (skipInterpreterRunMethod) {
                            skipInterpreterRunMethod = false;
                            continue;
                        }

                        InterpretedFrameInfo info = _interpretedFrames[_interpretedFrameIndex++];

                        if (info.DebugInfo != null) {
                            file = info.DebugInfo.FileName;
                            line = GetLine(info.DebugInfo, _loopLines, _interpretedFrames.Count - _interpretedFrameIndex);
                        } else {
                            file = null;
                            line = 0;
                        }
                        methodName = info.MethodName;

                        // TODO: We need some more general way to recognize and parse non-Ruby interpreted frames
                        TryParseRubyMethodName(ref methodName, ref file, ref line);

                        if (methodName == InterpretedCallSiteName) {
                            // ignore ruby interpreted call sites - the rule of a site a library method calls through
                            // when the DLR interprets it
                            if (_libraryRun > 0) {
                                _callSiteSinceLastLibrary = true;
                            }
                            continue;
                        }
                    } else if (skipCompiledCatchSite && IsRubyMethodFrame(frame)) {
                        skipCompiledCatchSite = false;
                        continue;
                    } else if (TryGetStackFrameInfo(frame, out methodName, out file, out line)) {
                        isClrFrame = _isClrFrame;
                        // special case: the frame will be added with the next frame's source info:
                        if (line == NextFrameLine) {
                            // MRI gives a C function a frame when it is called as a method - by method dispatch,
                            // rb_funcall, rb_block_call - and none when another C function calls it directly.
                            // Here the first is a library method calling a Ruby method through a call site, the
                            // second one library method calling another as a CLR method, which is reported as
                            // the outermost of them: Array#keep_if, not the Array#select! it is written with.
                            // A method of the same class called through a site is one MRI calls directly unless it has been
                            // redefined: Hash#[] calling Hash#default.
                            if (_libraryRun > 0 && (!_callSiteSinceLastLibrary || HaveSameOwner(_deferredMethodNames[_deferredMethodNames.Count - 1], methodName))) {
                                _deferredMethodNames[_deferredMethodNames.Count - 1] = methodName;
                            } else {
                                _deferredMethodNames.Add(methodName);
                                _libraryRun++;
                            }
                            _callSiteSinceLastLibrary = false;
                            continue;
                        }
                    } else {
                        if (_libraryRun > 0 && IsCallSiteFrame(frame.GetMethod())) {
                            _callSiteSinceLastLibrary = true;
                        }
                        continue;
                    }

                    if (!_keepInternalFrames && IsInternalFrameFile(file)) {
                        // Above the first frame reported, the frames it drops still count for caller(n): a method of the prelude
                        // skips its own frame that way (Kernel#caller_locations calls caller(n + 1)), and Thread#backtrace, which
                        // calls caller_locations, dropped that frame - so its caller's went instead.
                        int dropped = AddInternalFrame(methodName);
                        if (_trace.Count == 0 && !skippedAny) {
                            skipFrames = Math.Max(0, skipFrames - dropped);
                        }
                        continue;
                    }

                    foreach (var deferred in _deferredMethodNames) {
                        if (skipFrames == 0) {
                            _trace.Add(CreateFrame(FormatFrame(file, line, deferred)));
                        } else {
                            skipFrames--;
                            skippedAny = true;
                        }
                        // a library method's frame, reported where its caller is
                        _rubyFrameEnd = _trace.Count;
                    }
                    _deferredMethodNames.Clear();
                    _libraryRun = 0;

                    if (skipFrames == 0) {
                        _trace.Add(CreateFrame(FormatFrame(file, line, methodName)));
                    } else {
                        skipFrames--;
                        skippedAny = true;
                    }
                    if (!isClrFrame) {
                        _rubyFrameEnd = _trace.Count;
                    }
                    if (_rubyFrameEnd >= _maxFrames) {
                        // the first _maxFrames frames are what the whole walk would have reported first
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// On .NET 9 and later (its managed exception handling) an exception thrown from a catch or finally block has the method
        /// of the block twice in its own trace: the block - a funclet - and the method's body, which called it. A compiled Ruby
        /// method's ensure clause is such a block, so `def m; raise A; ensure; raise B; end' showed m twice (net8.0 did not, nor
        /// does a stack walk). The frame right below a frame of the same compiled method whose record says it was in an ensure
        /// clause (PrecompiledFrames.Frame.InEnsure) is that body. Not just any frame below one of the same method: compiled
        /// Ruby methods can call themselves with no frame in between.
        /// </summary>
        private static bool IsFuncletParent(MethodBase funclet, MethodBase method) {
            return funclet != null && method != null && method.Name.StartsWith(RubyMethodPrefix, StringComparison.Ordinal) &&
                (funclet == method || String.Equals(funclet.Name, method.Name, StringComparison.Ordinal) && funclet.DeclaringType == method.DeclaringType);
        }

        private static bool IsRubyMethodFrame(StackFrame/*!*/ frame) {
            MethodBase method = frame.GetMethod();
            return method != null && method.Name.StartsWith(RubyMethodPrefix, StringComparison.Ordinal);
        }

        private static bool IsInternalMethodFrame(string methodName) {
            return !String.IsNullOrEmpty(methodName) && !methodName.StartsWith("block ", StringComparison.Ordinal) && methodName[0] != '<';
        }

        /// <summary>
        /// A frame of the Ruby half of the core library (see <see cref="IsInternalFrameFile"/>), innermost first. A core method's
        /// frame is reported with its caller's location; a block of one is not reported, nor is a helper method (Math.__dom__,
        /// Enumerable#__extreme_by__, Math.__ir_sqrt__ - the Ruby half of a library method), which MRI writes as a C function
        /// the method calls directly.
        /// </summary>
        // Returns how many of the deferred frames it dropped.
        private int AddInternalFrame(string methodName) {
            if (!IsInternalMethodFrame(methodName)) {
                return 0;
            }
            int deferred = _deferredMethodNames.Count;
            string owner, name;
            SplitLabel(methodName, out owner, out name);

            // Every frame deferred since the last one reported was called by this method, directly or not. MRI's own method
            // would be C calling them directly, which gives them no frame (Kernel#sleep and Kernel#require wrapping the library
            // method of the same name, Array#uniq calling Array#each, Array#sum calling Enumerable#sum) - except for the generic
            // methods of Enumerable and the like, which call #each and #<=> as methods, so MRI reports those (Array#each below
            // Enumerable#min_by).
            if (!IsGenericModule(owner)) {
                _deferredMethodNames.Clear();
            } else if (_deferredMethodNames.Count > 0 && HaveSameOwner(_deferredMethodNames[_deferredMethodNames.Count - 1], methodName)) {
                // a method of its own module it is written with (Enumerator#next with #next_values), or the library method it
                // wraps (Enumerable#each_with_index): one method to MRI
                _deferredMethodNames.RemoveAt(_deferredMethodNames.Count - 1);
            }
            _libraryRun = 0;

            int dropped = deferred - _deferredMethodNames.Count;
            if (!IsHelperMethodName(name)) {
                _deferredMethodNames.Add(methodName);
            }
            return dropped;
        }

        // "Enumerable#map" -> ("Enumerable", "map"); "Math.sqrt" -> ("Math", "sqrt"); "foo" -> (null, "foo")
        private static void SplitLabel(string/*!*/ label, out string owner, out string/*!*/ name) {
            int i = label.LastIndexOfAny(_LabelSeparators);
            if (i <= 0) {
                owner = null;
                name = label;
            } else {
                owner = label.Substring(0, i);
                name = label.Substring(i + 1);
            }
        }

        private static readonly char[]/*!*/ _LabelSeparators = { '#', '.' };

        private static bool HaveSameOwner(string/*!*/ label1, string/*!*/ label2) {
            string owner1, owner2, name;
            SplitLabel(label1, out owner1, out name);
            SplitLabel(label2, out owner2, out name);
            return owner1 != null && owner1 == owner2;
        }

        // The modules whose methods work on any object that has #each (or #<=>) by calling it as a method.
        private static bool IsGenericModule(string owner) {
            return owner == "Enumerable" || owner == "Comparable" || owner == "Enumerator";
        }

        // __foo__ - but not the ones that are Ruby methods: BasicObject#__send__ and friends.
        private static bool IsHelperMethodName(string/*!*/ name) {
            return name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal) &&
                name != "__send__" && name != "__id__" && name != "__method__" && name != "__dir__" && name != "__callee__";
        }

        // The modules whose methods MRI defines with module_function and that code calls on the module: Math.sqrt. Kernel's are
        // usually called as instance methods (Integer("1")), so they keep "Kernel#Integer".
        private static bool IsModuleFunctionModule(string owner) {
            return owner == "Math" || owner == "Process" || owner == "FileTest" || owner == "GC" || owner == "ObjectSpace" ||
                owner == "Signal" || owner == "Marshal";
        }

        // A frame of method dispatch: what a library method calls a Ruby method through - the call site's rule, or the
        // dispatcher a call site's target is (IronRuby.Runtime.Calls.LibraryDispatcher and the like).
        private static bool IsCallSiteFrame(MethodBase method) {
            return method != null && (System.Runtime.CompilerServices.CallSiteHelpers.IsInternalFrame(method) ||
                method.DeclaringType != null && method.DeclaringType.Namespace == "IronRuby.Runtime.Calls");
        }

        /// <summary>
        /// A file whose frames are reported the way MRI reports a C method's (see <see cref="IsInternalFile"/>): also any file
        /// named "&lt;internal:...&gt;", as MRI does - RubyGems' Kernel#require is evaluated so.
        /// </summary>
        private static bool IsInternalFrameFile(string file) {
            return file != null && (file.StartsWith("<internal:", StringComparison.Ordinal) || IsInternalFile(file));
        }

        /// <summary>
        /// A frame put on top of <paramref name="backtrace"/> for the method an arity error was raised for:
        /// at the method's definition, or with no <paramref name="file"/> (a library method) where the
        /// backtrace's top frame is, as MRI reports a C method.
        /// </summary>
        internal static MutableString/*!*/ FormatCalleeFrame(RubyArray/*!*/ backtrace, string file, int line, string/*!*/ label) {
            if (file != null) {
                return MutableString.Create(FormatFrame(file, line, label), RubyEncoding.UTF8);
            }
            var top = (backtrace.Count > 0) ? backtrace[0] as MutableString : null;
            if (top == null) {
                return MutableString.Create("in '" + label + "'", RubyEncoding.UTF8);
            }
            string text = top.ToString();
            int at = text.IndexOf(":in '", StringComparison.Ordinal);
            return MutableString.Create((at < 0 ? text : text.Substring(0, at)) + ":in '" + label + "'", top.Encoding);
        }

        private static string/*!*/ FormatFrame(string file, int line, string methodName) {
            // an eval with a starting line below 1 (see RubyUtils.EncodeEvalLineOffset):
            line += RubyUtils.DecodeEvalLineOffset(ref file);

            if (String.IsNullOrEmpty(methodName)) {
                return String.Format("{0}:{1}", file, line);
            } else {
                // MRI 3.4 quotes the label with two apostrophes; it used to open with a backtick
                return String.Format("{0}:{1}:in '{2}'", file, line, methodName);
            }
        }

        private bool TryGetStackFrameInfo(StackFrame/*!*/ frame, out string/*!*/ methodName, out string/*!*/ fileName, out int line) {
            MethodBase method = frame.GetMethod();
            _isClrFrame = false;
            methodName = method.Name;

            fileName = (_needsClrFileInfo) ? GetFileName(frame) : null;
            var sourceLine = line = frame.GetFileLineNumber();

            if (TryParseRubyMethodName(ref methodName, ref fileName, ref line)) {
                Precompiled.PrecompiledCode.RebaseFrameFile(method, ref fileName);
                if (sourceLine == 0) {
                    RubyMethodDebugInfo debugInfo;
                    int mappedLine = 0;
                    if (RubyMethodDebugInfo.TryGet(method, out debugInfo)) {
                        var ilOffset = frame.GetILOffset();
                        if (ilOffset >= 0) {
                            mappedLine = debugInfo.Map(ilOffset);
                        }
                    } else if (_compiledFrames != null) {
                        mappedLine = TakeCompiledFrameLine(method.Name);
                    }
                    if (mappedLine > 0) {
                        line = mappedLine;
                    }
                }

                return true;
            } else {
                FrameAttributes frameAttributes = GetFrameAttributes(method);
                if (frameAttributes.Hidden) {
                    return false;
                }
                object[] attrs = frameAttributes.RubyMethods;
                if (attrs.Length > 0) {
                    // Ruby library method:
                    methodName = GetLibraryMethodLabel(method, attrs);

                    if (!_exceptionDetail) {
                        fileName = null;
                        line = NextFrameLine;
                    }

                    return true;
                } else if (_exceptionDetail || IsVisibleClrFrame(method)) {
                    // Visible CLR method:
                    _isClrFrame = true;
                    if (String.IsNullOrEmpty(fileName)) {
                        if (method.DeclaringType != null) {
                            fileName = (_needsClrFileInfo) ? GetAssemblyName(method.DeclaringType.Assembly) : null;
                            line = 0;
                        }
                    }
                    return true;
                } else {
                    // Invisible CLR method:
                    return false;
                }
            }
        }

        /// <summary>The attributes of a CLR method that decide how its frame is reported.</summary>
        private sealed class FrameAttributes {
            internal readonly bool Hidden;
            internal readonly object[]/*!*/ RubyMethods;

            internal FrameAttributes(MethodBase/*!*/ method) {
                Hidden = method.IsDefined(typeof(RubyStackTraceHiddenAttribute), false);
                RubyMethods = Hidden ? new object[0] : method.GetCustomAttributes(typeof(RubyMethodAttribute), false);
            }
        }

        // Reading custom attributes allocates and parses metadata on every call, and every frame of every walk asked
        // (most of a short walk - the one frame Thread.new records - went there). Weak, so a collectible method's
        // entry goes with it.
        private static readonly ConditionalWeakTable<MethodBase, FrameAttributes>/*!*/ _frameAttributes =
            new ConditionalWeakTable<MethodBase, FrameAttributes>();

        private static FrameAttributes/*!*/ GetFrameAttributes(MethodBase/*!*/ method) {
            return _frameAttributes.GetValue(method, m => new FrameAttributes(m));
        }

        private static readonly ConcurrentDictionary<MethodBase, string>/*!*/ _libraryMethodLabels =
            new ConcurrentDictionary<MethodBase, string>();

        /// <summary>
        /// MRI labels a builtin's frame with its owner, like any other method: "Integer#times",
        /// "Kernel#eval", "Integer.sqrt", "File::Stat#initialize". The owner is the module the
        /// library type defines or extends.
        /// </summary>
        private string/*!*/ GetLibraryMethodLabel(MethodBase/*!*/ method, object[]/*!*/ attrs) {
            string label;
            if (_libraryMethodLabels.TryGetValue(method, out label)) {
                return label;
            }

            RubyMethodAttribute chosen = null;
            foreach (RubyMethodAttribute attr in attrs) {
                if ((attr.MethodAttributes & RubyMethodAttributes.Instance) != 0) {
                    chosen = attr;
                    break;
                }
            }
            bool singleton = chosen == null;
            chosen = chosen ?? (RubyMethodAttribute)attrs[0];

            string owner = GetLibraryModuleName(method.DeclaringType);

            // a module function is both an instance method and a singleton one, and MRI names the one called, which a frame
            // here cannot tell: Math.sqrt, but Kernel#Integer
            if (!singleton && IsModuleFunctionModule(owner)) {
                foreach (RubyMethodAttribute attr in attrs) {
                    if ((attr.MethodAttributes & RubyMethodAttributes.Singleton) != 0 && attr.Name == chosen.Name) {
                        singleton = true;
                    }
                }
            }
            label = (owner != null) ? owner + (singleton ? "." : "#") + chosen.Name : chosen.Name;
            _libraryMethodLabels.TryAdd(method, label);
            return label;
        }

        private string GetLibraryModuleName(Type type) {
            if (type == null) {
                return null;
            }
            var attr = (RubyModuleAttribute)Attribute.GetCustomAttribute(type, typeof(RubyModuleAttribute), false);
            if (attr == null || attr is RubySingletonAttribute) {
                return null;
            }

            // Methods written once for several classes live in a module the classes copy in: IListOps
            // (a CLR interface) into Array, ClrFloat into Float, ClrInteger into Integer. To Ruby
            // they are the class's methods.
            string copiedInto = GetCopyingClassName(type.Assembly, attr.Extends != null && attr.Extends.IsInterface ? attr.Extends : type);
            if (copiedInto != null) {
                return copiedInto;
            }

            string name = attr.Name;
            if (name == null && attr.Extends != null) {
                try {
                    name = _context.GetModule(attr.Extends).Name;
                } catch (Exception) {
                    return null;
                }
            }
            if (name != null && attr.DefineIn != null) {
                string outer = GetLibraryModuleName(attr.DefineIn);
                if (outer != null) {
                    name = outer + "::" + name;
                }
            }
            return name;
        }

        // Per library assembly: copied-in module -> the class that copies it.
        private static readonly ConcurrentDictionary<Assembly, Dictionary<Type, Type>>/*!*/ _copyingClasses =
            new ConcurrentDictionary<Assembly, Dictionary<Type, Type>>();

        // The builtin class with a Ruby name of its own (Integer rather than System::Byte, say)
        // that copies the given module in, or null.
        private string GetCopyingClassName(Assembly/*!*/ assembly, Type/*!*/ included) {
            Type copying;
            return _copyingClasses.GetOrAdd(assembly, FindCopyingClasses).TryGetValue(included, out copying) ?
                GetLibraryModuleName(copying) : null;
        }

        // One pass over the assembly's attributes answers every later lookup: it reads the attributes
        // of every type in IronRuby.Libraries, and doing that once per copied module made each of the
        // first few backtraces of a process cost ~15 ms.
        private static Dictionary<Type, Type>/*!*/ FindCopyingClasses(Assembly/*!*/ assembly) {
            var result = new Dictionary<Type, Type>();
            Type[] types;
            try {
                types = assembly.GetTypes();
            } catch (ReflectionTypeLoadException) {
                return result;
            }
            foreach (Type candidate in types) {
                var attr = (RubyModuleAttribute)Attribute.GetCustomAttribute(candidate, typeof(RubyModuleAttribute), false);
                if (attr == null || attr.Name == null) {
                    continue;
                }
                foreach (IncludesAttribute includes in candidate.GetCustomAttributes(typeof(IncludesAttribute), false)) {
                    if (includes.Copy) {
                        foreach (Type included in includes.Types) {
                            // the first copying class in type order, as the per-module scan found it
                            if (!result.ContainsKey(included)) {
                                result.Add(included, candidate);
                            }
                        }
                    }
                }
            }
            return result;
        }

        internal static bool IsVisibleClrFrame(MethodBase/*!*/ method) {
            if (Microsoft.Scripting.Actions.DynamicSiteHelpers.IsInvisibleDlrStackFrame(method)) {
                return false;
            }

            // The entry of a JIT-specialized method (JitStub.BuildEntry, BuildDirectEntry): its fast
            // path raises nothing, and its slow path calls the method's generic body, which is the
            // frame that stands for the method.
            if (method.DeclaringType == null && (method.Name.StartsWith(Jit.JitStub.EntryNamePrefix, StringComparison.Ordinal) ||
                method.Name.StartsWith(Jit.JitStub.DirectEntryNamePrefix, StringComparison.Ordinal))) {
                return false;
            }

            Type type = method.DeclaringType;
            // System.Linq.Expressions' object[] delegate thunks in a JIT process without dynamic code
            if (type == null && !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported &&
                method.Name.StartsWith("Thunk", StringComparison.Ordinal)) {
                return false;
            }
            // the stub MethodInfo.Invoke makes for a method it calls often (see MethodBaseInvoker below)
            if (type == null && method.Name.StartsWith("InvokeStub_", StringComparison.Ordinal)) {
                return false;
            }
            // a lambda System.Linq.Expressions compiled without a name: a wrapper the runtime made (a fiber's body run
            // from compiled code put "0:in 'lambda_method66'" under the fiber's block), never a frame of Ruby code
            if (type == null && method.Name.StartsWith("lambda_method", StringComparison.Ordinal)) {
                return false;
            }
            if (type != null) {
                if (type.Assembly == typeof(RubyOps).Assembly || type.Namespace != null && 
                    (type.Namespace.StartsWith("IronRuby.StandardLibrary", StringComparison.Ordinal) ||
                    type.Namespace.StartsWith("IronRuby.Builtins", StringComparison.Ordinal) ||
                    // the parser (PrismAstBridge.Parse): an error found reading a file, such as an
                    // unknown magic-comment encoding, was reported as raised in ":0:in 'Parse'"
                    type.Namespace.StartsWith("IronRuby.Prism", StringComparison.Ordinal))) {
                    return false;
                }                

                // The bottom of every non-main thread's stack: the runtime's own thread entry
                // point, which calls the delegate Thread.Start was given.  It is not a Ruby frame
                // and MRI has nothing like it, so it does not belong in a backtrace.  .NET 8 never
                // showed it; .NET 10 does (Thread.StartCallback appears below the thread's block),
                // which otherwise adds a bogus "\tfrom :0:in 'StartCallback'" line to every
                // Thread#backtrace and to the report_on_exception output.
                if (type == typeof(Thread)) {
                    return false;
                }

                // A rethrow through ExceptionDispatchInfo keeps the frames the exception came through and
                // adds those it is rethrown from, the first of them ExceptionDispatchInfo.Throw itself: a
                // bookkeeping frame, never a Ruby one. The DLR's interpreter rethrows an exception that a
                // frame has no handler for this way, once the finally blocks on its way have run (upstream
                // IronLanguages/dlr #371), which put ":0:in 'Throw'" on top of every backtrace of an exception
                // leaving an interpreted frame. Ruby code calling ExceptionDispatchInfo.Throw gets the same.
                if (type == typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo)) {
                    return false;
                }

                // The runtime's helper for a tail call it cannot make directly (a compiled method's `tail.' call into a
                // library method with more arguments than its caller), which put "in 'DispatchTailCalls'" under the frame
                // of Kernel#Integer called from compiled code.
                if (type == typeof(RuntimeHelpers)) {
                    return false;
                }

                // MethodInfo.Invoke's own frames (RuntimeMethodHandle.InvokeMethod, MethodBaseInvoker): the interpreter
                // calls a delegate with more parameters than it has call instructions for (a Ruby method with more than
                // a dozen) through reflection, and rethrows what it raised from where it was raised.
                if (type == typeof(RuntimeMethodHandle) || type.Namespace == "System.Reflection" && type.Assembly == typeof(object).Assembly) {
                    return false;
                }

                // Without dynamic code (NativeAOT, or IsDynamicCodeSupported=false in a JIT process)
                // the DLR hands rules whose delegate type has no native thunk to System.Linq.Expressions'
                // interpreter, whose delegates call it through thunks in System.Dynamic.Utils and by
                // reflection, and it calls methods with value types in their signature by reflection,
                // which rethrows the callee's exception through ExceptionDispatchInfo. None of these
                // frames is there when rules are compiled.
                if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported && IsNoDynamicCodeHelperFrame(type)) {
                    return false;
                }
            }

            // TODO: check loaded assemblies?
            return true;
        }

        private static bool IsNoDynamicCodeHelperFrame(Type/*!*/ type) {
            if (type == typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo) || type == typeof(RuntimeMethodHandle)) {
                return true;
            }
            string ns = type.Namespace;
            return ns != null && (ns.StartsWith("System.Linq.Expressions", StringComparison.Ordinal) ||
                ns == "System.Dynamic.Utils" || ns == "System.Reflection");
        }
        // stringio.rb: MRI's StringIO is a C extension, so a method written here has no frame of its own either; nor
        // has a method of set.rb, which is the core class Set in MRI 4.0.
        private static readonly string[]/*!*/ _InternalFiles = { "/ironruby/ruby4.rb", "/ironruby/argf.rb", "/ironruby/thread.rb", "/ironruby/gem_compat.rb",
            "/ironruby/ironruby/rational.rb", "/ironruby/ironruby/complex.rb", "/ironruby/stringio.rb", "/ironruby/set.rb" };

        /// <summary>
        /// The Ruby half of the core library. MRI 3.4+ reports a frame of a core method written in
        /// Ruby (&lt;internal:kernel&gt; and the like) the way it reports a C method's: with the
        /// location of the code that called it. Blocks and file-level code inside such a file are
        /// implementation detail and not reported at all.
        /// </summary>
        public static bool IsInternalFile(string file) {
            if (file == null) {
                return false;
            }
            foreach (var suffix in _InternalFiles) {
                if (file.EndsWith(suffix, StringComparison.Ordinal) || file.Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal)) {
                    return true;
                }
            }
            return false;
        }

        internal const string TopLevelMethodName = "#"; 
        // Not ':': since 3.4 a method frame's label carries the owner ("M::C#foo"), which
        // contains colons, and the parser below splits on the first separator it finds.
        // U+2236 cannot occur in a label.
        private const char NamePartsSeparator = '\u2236';
        private const string RubyMethodPrefix = "\u2111\u211c:";
        private static int _Id = 0;
        internal const int MaxDebugModePathSize = 256; // PDB limit

        internal static string/*!*/ EncodeMethodName(string/*!*/ methodName, string sourcePath, SourceSpan location, bool debugMode) {
            return new StringBuilder().
                Append(RubyMethodPrefix).
                Append(methodName).
                Append(NamePartsSeparator).
                Append(location.IsValid ? location.Start.Line : 0).
                Append(NamePartsSeparator).
                Append(Interlocked.Increment(ref _Id)).
                Append(NamePartsSeparator).
                Append(debugMode && sourcePath != null && sourcePath.Length > MaxDebugModePathSize ? sourcePath.Substring(0, MaxDebugModePathSize) : sourcePath).
                ToString();
        }
        
        // \u2111\u211c:{method-name}:{line-number}:{unique-id}:{file-name}
        internal static bool TryParseRubyMethodName(ref string methodName, ref string fileName, ref int line) {
            if (methodName != null && methodName.StartsWith(RubyMethodPrefix, StringComparison.Ordinal)) {
                string encoded = methodName;

                // method name:
                int s = RubyMethodPrefix.Length;
                int e = encoded.IndexOf(NamePartsSeparator, s);
                if (e < 0) {
                    return false;
                }
                methodName = encoded.Substring(s, e - s);
                if (methodName == TopLevelMethodName) {
                    methodName = null;
                }

                // line number:
                s = e + 1;
                e = encoded.IndexOf(NamePartsSeparator, s);
                if (e < 0) {
                    return false;
                }
                if (line == 0 && !Int32.TryParse(encoded.Substring(s, e - s), out line)) {
                    return false;
                }

                // file name:
                s = e + 1;
                e = encoded.IndexOf(NamePartsSeparator, s);
                if (e < 0) {
                    return false;
                }
                
                if (fileName == null) {
                    fileName = encoded.Substring(e + 1);
                }
                return true;
            }
            return false;
        }
    }
}

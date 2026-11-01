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
using System.Security;
using System.Text;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Interpreter;

namespace IronRuby.Runtime {
    public sealed class RubyStackTraceBuilder {
#if FEATURE_STACK_TRACE
        private readonly RubyArray/*!*/ _trace;
        private readonly bool _hasFileAccessPermission;
        private readonly bool _needsClrFileInfo;
        private readonly bool _exceptionDetail;
        private readonly RubyEncoding/*!*/ _encoding;
        private IList<InterpretedFrameInfo> _interpretedFrames;
        private int _interpretedFrameIndex;
        // Frames reported with the source info of the next frame out that has its own: a library
        // method's, and a core method written in Ruby (see IsInternalFile). Innermost first.
        private readonly List<string>/*!*/ _deferredMethodNames = new List<string>();
        private bool _lastDeferredIsLibrary;
        private readonly RubyContext/*!*/ _context;
        // TracePoint's c_call location needs the frame that really made the call, which inside a
        // core method written in Ruby is that method's own, not its caller's.
        private bool _keepInternalFrames;

        private RubyStackTraceBuilder(RubyContext/*!*/ context)
            : this(context, context.GetPathEncoding()) {
        }

        private RubyStackTraceBuilder(RubyContext/*!*/ context, RubyEncoding/*!*/ encoding) {
            _context = context;
            _needsClrFileInfo = NeedsClrFileInfo(context);
            _hasFileAccessPermission = _needsClrFileInfo && DetectFileAccessPermissions();
            _exceptionDetail = context.Options.ExceptionDetail;
            _encoding = encoding;
            _trace = new RubyArray();
        }

        internal RubyStackTraceBuilder(RubyContext/*!*/ context, Exception/*!*/ exception, StackTrace catchSiteTrace, bool isCatchSiteInterpreted)
            : this(context) {
            // Compiled trace: contains frames starting with the throw site up to the first filter/catch that the exception was caught by:
            StackTrace throwSiteTrace = GetClrStackTrace(exception, _needsClrFileInfo);
            _interpretedFrames = InterpretedFrame.GetExceptionStackTrace(exception);
            AddExceptionBacktrace(throwSiteTrace, catchSiteTrace, isCatchSiteInterpreted);
        }

        private RubyStackTraceBuilder(CapturedTrace/*!*/ captured)
            : this(captured.Context, captured.Encoding) {
            _interpretedFrames = captured.InterpretedFrames;
            AddExceptionBacktrace(captured.ThrowSiteTrace, captured.CatchSiteTrace, captured.IsCatchSiteInterpreted);
        }

        private void AddExceptionBacktrace(StackTrace/*!*/ throwSiteTrace, StackTrace catchSiteTrace, bool isCatchSiteInterpreted) {
            AddBacktrace(throwSiteTrace.GetFrames(), 0, false);

            // Compiled trace: contains frames above and including the first Ruby filter/catch site that the exception was caught by:
            if (catchSiteTrace != null) {
                // Interpreted: skip one interpreter Run method frame - is was already matched with the last processed interpreted frame:
                // Compiled: skip one frame - the catch-site frame is already included
                AddBacktrace(catchSiteTrace.GetFrames(), isCatchSiteInterpreted ? 0 : 1, isCatchSiteInterpreted);
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
            internal readonly bool IsCatchSiteInterpreted;

            internal CapturedTrace(RubyContext/*!*/ context, Exception/*!*/ exception, StackTrace catchSiteTrace, bool isCatchSiteInterpreted) {
                Context = context;
                Encoding = context.GetPathEncoding();
                // the exception's own trace grows as it is rethrown, and a raise of it starts it over:
                ThrowSiteTrace = GetClrStackTrace(exception, NeedsClrFileInfo(context));
                // the interpreter's frames are saved on the exception once, and raise clears them:
                InterpretedFrames = InterpretedFrame.GetExceptionStackTrace(exception);
                CatchSiteTrace = catchSiteTrace;
                IsCatchSiteInterpreted = isCatchSiteInterpreted;
            }

            internal RubyArray/*!*/ Build() {
                return new RubyStackTraceBuilder(this).RubyTrace;
            }
        }

        internal RubyStackTraceBuilder(RubyContext/*!*/ context, int skipFrames)
            : this(context, skipFrames, false) {
        }

        internal RubyStackTraceBuilder(RubyContext/*!*/ context, int skipFrames, bool keepInternalFrames)
            : this(context) {
            _keepInternalFrames = keepInternalFrames;
            var trace = GetClrStackTrace(null, _needsClrFileInfo);

            _interpretedFrames = InterpretedFrame.CurrentFrame.Value != null ?
                new List<InterpretedFrameInfo>(InterpretedFrame.CurrentFrame.Value.GetStackTraceDebugInfo()) :
                null;

            AddBacktrace(trace.GetFrames(), skipFrames, false);
        }

        /// <summary>
        /// Builds a backtrace for a thread other than the caller's from that thread's interpreted frame
        /// chain, which the interpreter maintains as a linked list hanging off a thread-local and which
        /// any thread may read.  A CLR stack walk is not an option here - .NET Core has no
        /// StackTrace(Thread) and no Thread.Suspend - so this is what there is, and it costs nothing
        /// when nobody asks: the chain is already there for the owning thread's own backtraces.
        ///
        /// The thread is running while we read, so the result is a snapshot rather than an instant.
        /// Frames are not recycled, so a frame that was left while we were walking still points at its
        /// old parent and the walk terminates; the depth cap is there for the case where it somehow
        /// does not.
        /// </summary>
        internal RubyStackTraceBuilder(RubyContext/*!*/ context, InterpretedFrame frame)
            : this(context) {

            for (int depth = 0; frame != null && depth < MaxThreadBacktraceDepth; depth++, frame = frame.Parent) {
                string methodName = frame.Name;
                if (methodName == InterpretedCallSiteName) {
                    continue;
                }

                string file;
                int line;
                var debugInfo = frame.GetDebugInfo(frame.InstructionIndex);
                if (debugInfo != null) {
                    file = debugInfo.FileName;
                    line = debugInfo.StartLine;
                } else {
                    file = null;
                    line = 0;
                }

                // Frames the interpreter runs on behalf of the DLR rather than of Ruby have no encoded
                // Ruby name; they are not part of a Ruby backtrace and MRI has nothing to put in their
                // place, so leave them out.
                if (!TryParseRubyMethodName(ref methodName, ref file, ref line)) {
                    continue;
                }

                if (IsInternalFile(file)) {
                    if (IsInternalMethodFrame(methodName)) {
                        _deferredMethodNames.Add(methodName);
                    }
                    continue;
                }

                foreach (var deferred in _deferredMethodNames) {
                    _trace.Add(MutableString.Create(FormatFrame(file, line, deferred), _encoding));
                }
                _deferredMethodNames.Clear();

                _trace.Add(MutableString.Create(FormatFrame(file, line, methodName), _encoding));
            }
        }

        private const int MaxThreadBacktraceDepth = 10000;

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
#if FEATURE_STACK_TRACE
            var frame = InterpretedFrame.CurrentFrame.Value;
            for (int depth = 0; frame != null && depth < MaxThreadBacktraceDepth; depth++, frame = frame.Parent) {
                string methodName = frame.Name;
                if (methodName == InterpretedCallSiteName) {
                    continue;
                }

                string frameFile;
                int frameLine;
                var debugInfo = frame.GetDebugInfo(frame.InstructionIndex);
                if (debugInfo != null) {
                    frameFile = debugInfo.FileName;
                    frameLine = debugInfo.StartLine;
                } else {
                    frameFile = null;
                    frameLine = 0;
                }

                if (!TryParseRubyMethodName(ref methodName, ref frameFile, ref frameLine)) {
                    continue;
                }

                // A frame of the Ruby half of the core library is reported at its caller's location,
                // so it is not the answer either - keep walking out.
                if (IsInternalFile(frameFile)) {
                    continue;
                }

                if (frameFile == null) {
                    return false;
                }

                file = frameFile;
                line = frameLine;
                return true;
            }
#endif
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
        private static bool NeedsClrFileInfo(RubyContext/*!*/ context) {
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

        private void InitializeInterpretedFrames() {
        }

        public RubyArray/*!*/ RubyTrace {
            get { return _trace; }
        }

        private const int NextFrameLine = Int32.MinValue;
        internal const string InterpretedCallSiteName = "CallSite.Target";

        private void AddBacktrace(IEnumerable<StackFrame> stackTrace, int skipFrames, bool skipInterpreterRunMethod) {
            if (stackTrace != null) {
                foreach (var frame in InterpretedFrame.GroupStackFrames(stackTrace)) {
                    string methodName, file;
                    int line;

                    if (_interpretedFrames != null && _interpretedFrameIndex < _interpretedFrames.Count
                        && InterpretedFrame.IsInterpretedFrame(frame.GetMethod())) {

                        if (skipInterpreterRunMethod) {
                            skipInterpreterRunMethod = false;
                            continue;
                        }

                        InterpretedFrameInfo info = _interpretedFrames[_interpretedFrameIndex++];

                        if (info.DebugInfo != null) {
                            file = info.DebugInfo.FileName;
                            line = info.DebugInfo.StartLine;
                        } else {
                            file = null;
                            line = 0;
                        }
                        methodName = info.MethodName;

                        // TODO: We need some more general way to recognize and parse non-Ruby interpreted frames
                        TryParseRubyMethodName(ref methodName, ref file, ref line);

                        if (methodName == InterpretedCallSiteName) {
                            // ignore ruby interpreted call sites
                            continue;
                        }
                    } else if (TryGetStackFrameInfo(frame, out methodName, out file, out line)) {
                        // special case: the frame will be added with the next frame's source info:
                        if (line == NextFrameLine) {
                            // of library methods calling one another only the outermost is reported
                            if (_lastDeferredIsLibrary) {
                                _deferredMethodNames[_deferredMethodNames.Count - 1] = methodName;
                            } else {
                                _deferredMethodNames.Add(methodName);
                            }
                            _lastDeferredIsLibrary = true;
                            continue;
                        }
                    } else {
                        continue;
                    }

                    if (!_keepInternalFrames && IsInternalFile(file)) {
                        if (IsInternalMethodFrame(methodName)) {
                            _deferredMethodNames.Add(methodName);
                            _lastDeferredIsLibrary = false;
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
                    _lastDeferredIsLibrary = false;

                    if (skipFrames == 0) {
                        _trace.Add(MutableString.Create(FormatFrame(file, line, methodName), _encoding));
                    } else {
                        skipFrames--;
                    }
                }
            }
        }

        private static bool IsInternalMethodFrame(string methodName) {
            return !String.IsNullOrEmpty(methodName) && !methodName.StartsWith("block ", StringComparison.Ordinal) && methodName[0] != '<';
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
            methodName = method.Name;

            fileName = (_hasFileAccessPermission) ? GetFileName(frame) : null;
            var sourceLine = line = frame.GetFileLineNumber();

            if (TryParseRubyMethodName(ref methodName, ref fileName, ref line)) {
                if (sourceLine == 0) {
                    RubyMethodDebugInfo debugInfo;
                    if (RubyMethodDebugInfo.TryGet(method, out debugInfo)) {
                        var ilOffset = frame.GetILOffset();
                        if (ilOffset >= 0) {
                            var mappedLine = debugInfo.Map(ilOffset);
                            if (mappedLine != 0) {
                                line = mappedLine;
                            }
                        }
                    }
                }

                return true;
            } else if (method.IsDefined(typeof(RubyStackTraceHiddenAttribute), false)) {
                return false;
            } else {
                object[] attrs = method.GetCustomAttributes(typeof(RubyMethodAttribute), false);
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
                    if (String.IsNullOrEmpty(fileName)) {
                        if (method.DeclaringType != null) {
                            fileName = (_hasFileAccessPermission) ? GetAssemblyName(method.DeclaringType.Assembly) : null;
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

            // a module function is registered twice, as an instance method and a singleton one, and
            // MRI reports the instance method
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

        private static readonly ConcurrentDictionary<Type, Type>/*!*/ _copyingClasses = new ConcurrentDictionary<Type, Type>();

        // The builtin class with a Ruby name of its own (Integer rather than System::Byte, say)
        // that copies the given module in, or null.
        private string GetCopyingClassName(Assembly/*!*/ assembly, Type/*!*/ included) {
            Type copying;
            if (!_copyingClasses.TryGetValue(included, out copying)) {
                copying = FindCopyingClass(assembly, included);
                _copyingClasses.TryAdd(included, copying);
            }
            return copying != null ? GetLibraryModuleName(copying) : null;
        }

        private static Type FindCopyingClass(Assembly/*!*/ assembly, Type/*!*/ included) {
            Type[] types;
            try {
                types = assembly.GetTypes();
            } catch (ReflectionTypeLoadException) {
                return null;
            }
            foreach (Type candidate in types) {
                var attr = (RubyModuleAttribute)Attribute.GetCustomAttribute(candidate, typeof(RubyModuleAttribute), false);
                if (attr == null || attr.Name == null) {
                    continue;
                }
                foreach (IncludesAttribute includes in candidate.GetCustomAttributes(typeof(IncludesAttribute), false)) {
                    if (includes.Copy && Array.IndexOf(includes.Types, included) >= 0) {
                        return candidate;
                    }
                }
            }
            return null;
        }

        private static bool IsVisibleClrFrame(MethodBase/*!*/ method) {
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
            if (type != null) {
                if (type.Assembly == typeof(RubyOps).Assembly || type.Namespace != null && 
                    (type.Namespace.StartsWith("IronRuby.StandardLibrary", StringComparison.Ordinal) ||
                    type.Namespace.StartsWith("IronRuby.Builtins", StringComparison.Ordinal))) {
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

                // Without dynamic code (NativeAOT) the DLR hands rules whose delegate type has no
                // native thunk to System.Linq.Expressions' interpreter, and calls methods with value
                // types in their signature by reflection, which rethrows the callee's exception
                // through ExceptionDispatchInfo. Neither frame is there in a JIT process.
                if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported &&
                    (type == typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo) ||
                     type.Namespace != null && type.Namespace.StartsWith("System.Linq.Expressions", StringComparison.Ordinal))) {
                    return false;
                }
            }

            // TODO: check loaded assemblies?
            return true;
        }

        // TODO: partial trust
        private static bool DetectFileAccessPermissions() {
#if FEATURE_FILESYSTEM
            return true;
#else
            return false;
#endif
        }
#endif
        private static readonly string[]/*!*/ _InternalFiles = { "/ironruby/ruby4.rb", "/ironruby/argf.rb", "/ironruby/thread.rb", "/ironruby/gem_compat.rb" };

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

        private static string ParseRubyMethodName(string/*!*/ lambdaName) {
            if (!lambdaName.StartsWith(RubyMethodPrefix, StringComparison.Ordinal)) {
                return lambdaName;
            }

            int nameEnd = lambdaName.IndexOf(NamePartsSeparator, RubyMethodPrefix.Length);
            string name = lambdaName.Substring(RubyMethodPrefix.Length, nameEnd - RubyMethodPrefix.Length);
            return (name != TopLevelMethodName) ? name : null;
        }
    }
}

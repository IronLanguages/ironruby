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
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {

    /// <summary>
    /// TracePoint. Delivered events: :line, :call, :return, :b_call, :b_return, :class, :end,
    /// :raise (from Kernel#raise), :rescue, :thread_begin, :thread_end and :script_compiled
    /// (Kernel#eval). :c_call, :c_return and :fiber_switch are accepted but never delivered.
    /// </summary>
    [RubyClass("TracePoint", Extends = typeof(TracePoint), Inherits = typeof(object), Restrictions = ModuleRestrictions.Builtin | ModuleRestrictions.NoUnderlyingType)]
    [UndefineMethod("allocate", IsStatic = true)]
    public static class TracePointOps {
        private static readonly string[] _eventNames = {
            "line", "class", "end", "call", "return", "c_call", "c_return", "raise",
            "b_call", "b_return", "thread_begin", "thread_end", "fiber_switch", "script_compiled", "rescue"
        };

        #region Construction

        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        public static TracePoint/*!*/ Create(RespondToStorage/*!*/ respondTo, UnaryOpStorage/*!*/ toSym,
            BlockParam handler, RubyClass/*!*/ self, params object[]/*!*/ events) {

            TraceEvents mask = ParseEvents(respondTo, toSym, events);
            if (handler == null) {
                throw RubyExceptions.CreateArgumentError("must be called with a block");
            }
            return new TracePoint(self.Context, mask, handler.Proc);
        }

        [RubyMethod("trace", RubyMethodAttributes.PublicSingleton)]
        public static TracePoint/*!*/ Trace(RespondToStorage/*!*/ respondTo, UnaryOpStorage/*!*/ toSym,
            BlockParam handler, RubyClass/*!*/ self, params object[]/*!*/ events) {

            var result = Create(respondTo, toSym, handler, self, events);
            result.Enable();
            return result;
        }

        private static TraceEvents ParseEvents(RespondToStorage/*!*/ respondTo, UnaryOpStorage/*!*/ toSym, object[]/*!*/ events) {
            if (events.Length == 0) {
                return TraceEvents.All;
            }

            TraceEvents result = TraceEvents.None;
            foreach (object e in events) {
                result |= ParseEvent(respondTo, toSym, e);
            }
            return result;
        }

        private static TraceEvents ParseEvent(RespondToStorage/*!*/ respondTo, UnaryOpStorage/*!*/ toSym, object e) {
            var context = toSym.Context;
            object symbol = e;
            if (!(symbol is RubySymbol) && !(symbol is MutableString)) {
                if (!Protocols.RespondTo(respondTo, e, "to_sym")) {
                    throw RubyExceptions.CreateImplicitConversionError(context.GetClassDisplayName(e), "Symbol");
                }
                var site = toSym.GetCallSite("to_sym");
                symbol = site.Target(site, e);
                if (!(symbol is RubySymbol)) {
                    throw RubyExceptions.CreateTypeError("can't convert {0} to Symbol ({0}#to_sym gives {1})",
                        context.GetClassDisplayName(e), context.GetClassDisplayName(symbol));
                }
            }

            string name = symbol.ToString();
            int index = Array.IndexOf(_eventNames, name);
            if (index >= 0) {
                return (TraceEvents)(1 << index);
            }

            switch (name) {
                case "a_call": return TraceEvents.Call | TraceEvents.BCall | TraceEvents.CCall;
                case "a_return": return TraceEvents.Return | TraceEvents.BReturn | TraceEvents.CReturn;
            }
            throw RubyExceptions.CreateArgumentError("unknown event: {0}", name);
        }

        #endregion

        #region stat, allow_reentry

        // MRI answers internal hook-list statistics keyed by the VM; there is nothing comparable to report.
        [RubyMethod("stat", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ Stat(RubyClass/*!*/ self) {
            return new Hash(self.Context);
        }

        [RubyMethod("allow_reentry", RubyMethodAttributes.PublicSingleton)]
        public static object AllowReentry([NotNull]BlockParam/*!*/ block, RubyClass/*!*/ self) {
            TraceEventInfo current = TracePoint.CurrentEvent;
            if (current == null) {
                throw RubyExceptions.CreateRuntimeError("No need to allow reentrance.");
            }

            TracePoint.CurrentEvent = null;
            try {
                object result;
                block.Yield(out result);
                return result;
            } finally {
                TracePoint.CurrentEvent = current;
            }
        }

        #endregion

        #region enable, disable, enabled?

        [RubyMethod("enable")]
        public static object Enable(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, TracePoint/*!*/ self,
            [Keywords, Optional]IDictionary<object, object> options) {

            object target = null, targetLine = null, targetThread = null;
            bool defaultThread = true;
            if (options != null) {
                foreach (var entry in options) {
                    string key = entry.Key is RubySymbol ? entry.Key.ToString() : null;
                    switch (key) {
                        case "target": target = entry.Value; break;
                        case "target_line": targetLine = entry.Value; break;
                        case "target_thread": targetThread = entry.Value; defaultThread = false; break;
                        default:
                            throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", self.Context.Inspect(entry.Key));
                    }
                }
            }

            // MRI: with a block and no target, the TracePoint only traces the thread that enabled it.
            if (defaultThread || targetThread is RubySymbol && targetThread.ToString() == "default") {
                targetThread = (target == null && targetLine == null && block != null) ? RubyUtils.CurrentRubyThread : null;
            }

            bool wasEnabled = self.IsEnabled;

            if (targetThread != null) {
                var thread = targetThread as Thread;
                if (thread == null) {
                    throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected VM/thread)", self.Context.GetClassDisplayName(targetThread));
                }
                if (self.TargetThread != null && self.TargetThread != thread) {
                    throw RubyExceptions.CreateArgumentError("can not override target_thread filter");
                }
            }

            if (target == null) {
                if (targetLine != null) {
                    throw RubyExceptions.CreateArgumentError("only target_line is specified");
                }
                if (self.HasTarget) {
                    throw RubyExceptions.CreateArgumentError("can't nest-enable a targeting TracePoint");
                }
                self.TargetThread = (Thread)targetThread;
                self.Enable();
            } else {
                var traceTarget = MakeTarget(fixnumCast, self, target, targetLine);
                self.TargetThread = (Thread)targetThread;
                self.EnableForTarget(traceTarget);
            }

            if (block == null) {
                return ScriptingRuntimeHelpers.BooleanToObject(wasEnabled);
            }

            try {
                object result;
                block.Yield(out result);
                return result;
            } finally {
                if (wasEnabled) {
                    self.Enable();
                } else {
                    self.Disable();
                }
            }
        }

        private static TraceTarget/*!*/ MakeTarget(ConversionStorage<int>/*!*/ fixnumCast, TracePoint/*!*/ self, object target, object targetLine) {
            RubyMemberInfo method = null;
            Proc proc = null;

            if (target is RubyMethod) {
                method = ((RubyMethod)target).Info;
            } else if (target is UnboundMethod) {
                method = ((UnboundMethod)target).Info;
            } else {
                proc = target as Proc;
            }

            var lambdaMethod = method as RubyLambdaMethodInfo;
            if (lambdaMethod != null) {
                proc = lambdaMethod.Lambda;
                method = null;
            }

            var rubyMethod = method as RubyMethodInfo;
            if (rubyMethod == null && proc == null) {
                throw RubyExceptions.CreateArgumentError("specified target is not supported");
            }

            if (self.IsEnabled) {
                throw RubyExceptions.CreateArgumentError("can't nest-enable a targeting TracePoint");
            }

            int line = 0;
            if (targetLine != null) {
                if ((self.Events & TraceEvents.Line) == 0) {
                    throw RubyExceptions.CreateArgumentError("target_line is specified, but line event is not specified");
                }
                line = Protocols.CastToFixnum(fixnumCast, targetLine);
            }

            // The events the target's code can produce; MRI refuses a target that would never fire.
            TraceEvents possible;
            int firstLine, lastLine;
            if (rubyMethod != null) {
                possible = TraceEvents.All & ~(TraceEvents.ThreadBegin | TraceEvents.ThreadEnd | TraceEvents.ScriptCompiled);
                if (rubyMethod.GetSyntaxTree().Body.Statements.Count == 0) {
                    possible &= ~TraceEvents.Line;
                }
                firstLine = rubyMethod.SourceSpan.Start.Line;
                lastLine = rubyMethod.SourceSpan.End.Line;
            } else {
                possible = TraceEvents.All & ~(TraceEvents.Call | TraceEvents.Return | TraceEvents.ThreadBegin | TraceEvents.ThreadEnd | TraceEvents.ScriptCompiled);
                firstLine = proc.Dispatcher.SourceLine;
                lastLine = Int32.MaxValue;
            }

            if ((self.Events & possible) == 0 || targetLine != null && (line < firstLine || line > lastLine)) {
                throw RubyExceptions.CreateArgumentError("can not enable any hooks");
            }

            return (rubyMethod != null)
                ? new TraceTarget(rubyMethod.DeclaringModule, rubyMethod.DefinitionName, line)
                : new TraceTarget(proc.Dispatcher, line);
        }

        [RubyMethod("disable")]
        public static object Disable(BlockParam block, TracePoint/*!*/ self) {
            bool wasEnabled = self.IsEnabled;
            if (block == null) {
                self.Disable();
                return ScriptingRuntimeHelpers.BooleanToObject(wasEnabled);
            }

            if (self.HasTarget) {
                throw RubyExceptions.CreateArgumentError("can't disable a targeting TracePoint in a block");
            }

            Thread targetThread = self.TargetThread;
            self.Disable();
            try {
                object result;
                block.Yield(out result);
                return result;
            } finally {
                if (wasEnabled) {
                    self.TargetThread = targetThread;
                    self.Enable();
                }
            }
        }

        [RubyMethod("enabled?")]
        public static bool IsEnabled(TracePoint/*!*/ self) {
            return self.IsEnabled;
        }

        #endregion

        #region Event attributes

        private static TraceEventInfo/*!*/ GetEvent(TracePoint/*!*/ self) {
            TraceEventInfo info = TracePoint.CurrentEvent;
            if (info == null) {
                throw RubyExceptions.CreateRuntimeError("access from outside");
            }
            return info;
        }

        private static TraceEventInfo/*!*/ GetEvent(TracePoint/*!*/ self, TraceEvents supported) {
            TraceEventInfo info = GetEvent(self);
            if ((info.Event & supported) == 0) {
                throw RubyExceptions.CreateRuntimeError("not supported by this event");
            }
            return info;
        }

        [RubyMethod("event")]
        public static RubySymbol/*!*/ GetEventName(TracePoint/*!*/ self) {
            return self.Context.CreateAsciiSymbol(EventName(GetEvent(self).Event));
        }

        private static string/*!*/ EventName(TraceEvents e) {
            int index = 0;
            while ((1 << index) != (int)e) {
                index++;
            }
            return _eventNames[index];
        }

        [RubyMethod("lineno")]
        public static int GetLine(TracePoint/*!*/ self) {
            return GetEvent(self).Line;
        }

        [RubyMethod("path")]
        public static MutableString GetPath(TracePoint/*!*/ self) {
            var info = GetEvent(self);
            return info.Path != null ? self.Context.EncodePath(info.Path) : null;
        }

        [RubyMethod("method_id")]
        public static RubySymbol GetMethodId(TracePoint/*!*/ self) {
            var info = GetEvent(self);
            TracePoint.ResolveMethod(info);
            return info.MethodName != null ? self.Context.EncodeIdentifier(info.MethodName) : null;
        }

        [RubyMethod("callee_id")]
        public static RubySymbol GetCalleeId(TracePoint/*!*/ self) {
            var info = GetEvent(self);
            TracePoint.ResolveMethod(info);
            string name = info.CalleeName ?? info.MethodName;
            return name != null ? self.Context.EncodeIdentifier(name) : null;
        }

        [RubyMethod("defined_class")]
        public static RubyModule GetDefinedClass(TracePoint/*!*/ self) {
            var info = GetEvent(self);
            TracePoint.ResolveMethod(info);
            return info.DefinedClass;
        }

        [RubyMethod("binding")]
        public static Binding GetBinding(TracePoint/*!*/ self) {
            var info = GetEvent(self);
            return info.Scope != null ? Binding.Create(info.Scope, info.Scope.SelfObject) : null;
        }

        [RubyMethod("self")]
        public static object GetSelf(TracePoint/*!*/ self) {
            return GetEvent(self).Self;
        }

        [RubyMethod("return_value")]
        public static object GetReturnValue(TracePoint/*!*/ self) {
            return GetEvent(self, TraceEvents.Return | TraceEvents.CReturn | TraceEvents.BReturn).ReturnValue;
        }

        [RubyMethod("raised_exception")]
        public static object GetRaisedException(TracePoint/*!*/ self) {
            return GetEvent(self, TraceEvents.Raise | TraceEvents.Rescue).Exception;
        }

        [RubyMethod("eval_script")]
        public static MutableString GetEvalScript(TracePoint/*!*/ self) {
            return GetEvent(self, TraceEvents.ScriptCompiled).EvalScript;
        }

        // IronRuby has no instruction sequences.
        [RubyMethod("instruction_sequence")]
        public static object GetInstructionSequence(TracePoint/*!*/ self) {
            GetEvent(self, TraceEvents.ScriptCompiled);
            throw new NotImplementedError("TracePoint#instruction_sequence is not supported");
        }

        [RubyMethod("parameters")]
        public static RubyArray GetParameters(TracePoint/*!*/ self) {
            var info = GetEvent(self, TraceEvents.Call | TraceEvents.Return | TraceEvents.BCall | TraceEvents.BReturn | TraceEvents.CCall | TraceEvents.CReturn);

            if ((info.Event & (TraceEvents.BCall | TraceEvents.BReturn)) != 0) {
                var blockScope = info.Scope as RubyBlockScope;
                if (blockScope == null) {
                    return new RubyArray();
                }
                var proc = blockScope.BlockFlowControl.Proc;
                return ProcOps.GetParameters(self.Context, proc, ArrayUtils.EmptyObjects);
            }

            TracePoint.ResolveMethod(info);
            return info.Method != null ? MethodReflection.GetParameters(self.Context, info.Method, info.MethodName ?? "") : new RubyArray();
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(TracePoint/*!*/ self) {
            TraceEventInfo info = TracePoint.CurrentEvent;
            if (info == null) {
                return MutableString.CreateAscii(self.IsEnabled ? "#<TracePoint:enabled>" : "#<TracePoint:disabled>");
            }

            var context = self.Context;
            var result = MutableString.CreateMutable(RubyEncoding.UTF8);
            result.Append("#<TracePoint:").Append(EventName(info.Event));

            switch (info.Event) {
                case TraceEvents.ThreadBegin:
                case TraceEvents.ThreadEnd:
                    result.Append(' ').Append(context.Inspect(info.Self));
                    return result.Append('>');

                case TraceEvents.Call:
                case TraceEvents.CCall:
                case TraceEvents.Return:
                case TraceEvents.CReturn:
                    TracePoint.ResolveMethod(info);
                    result.Append(" '").Append(info.MethodName ?? "").Append('\'');
                    break;
            }

            result.Append(' ').Append(info.Path ?? "").Append(':').Append(info.Line.ToString());

            if (info.Event == TraceEvents.Line) {
                TracePoint.ResolveMethod(info);
                if (info.MethodName != null) {
                    result.Append(" in '").Append(info.MethodName).Append('\'');
                }
            }
            return result.Append('>');
        }

        #endregion
    }
}

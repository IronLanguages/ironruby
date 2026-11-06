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

using MSA = System.Linq.Expressions;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using IronRuby.Builtins;
using Range = IronRuby.Builtins.Range;
using IronRuby.Compiler;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Interpreter;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Runtime {
    [ReflectionCached, CLSCompliant(false)]
    public static partial class RubyOps {
        [Emitted]
        public static readonly object DefaultArgument = new object();
        
        // Returned by a virtual site if a base call should be performed.
        [Emitted]
        public static readonly object ForwardToBase = new object();

        // How many refine blocks are running in the process (RubyUtils.EvaluateRefinementBlock). While none is, no Proc
        // has a refine block's override, so the lexical walk steps over block scopes (RubyScope.GetLexicalRefinements),
        // and the rule of a refined call made in a block written in a method can test the method's record instead
        // (RubyCallAction.Resolve) - with this test.
        [Emitted]
        public static int RefineBlocksRunning;

        // an instance of a dummy type that causes any rule based on instance type check to fail
        private sealed class _NeedsUpdate {
        }

        [Emitted]
        public static readonly object NeedsUpdate = new _NeedsUpdate();

        #region Scopes

        [Emitted]
        public static MutableTuple GetLocals(RubyScope/*!*/ scope) {
            return scope.Locals;
        }

        [Emitted]
        public static MutableTuple GetParentLocals(RubyScope/*!*/ scope) {
            return scope.Parent.Locals;
        }

        [Emitted]
        public static RubyScope/*!*/ GetParentScope(RubyScope/*!*/ scope) {
            return scope.Parent;
        }

        [Emitted]
        public static Proc GetMethodBlockParameter(RubyScope/*!*/ scope) {
            var methodScope = scope.GetInnerMostMethodScope();
            return methodScope != null ? methodScope.BlockParameter : null;
        }

        [Emitted]
        public static object GetMethodBlockParameterSelf(RubyScope/*!*/ scope) {
            Proc proc = scope.GetInnerMostMethodScope().BlockParameter;
            Debug.Assert(proc != null, "CreateBfcForYield is called before this method and it checks non-nullity");
            return proc.Self;
        }

        [Emitted]
        public static object GetProcSelf(Proc/*!*/ proc) {
            return proc.Self;
        }

        [Emitted]
        public static int GetProcArity(Proc/*!*/ proc) {
            return proc.Dispatcher.Arity;
        }

        [Emitted]
        public static void InitializeScope(RubyScope/*!*/ scope, MutableTuple locals, string[] variableNames, 
            InterpretedFrame interpretedFrame) {

            if (!scope.LocalsInitialized) {
                scope.SetLocals(locals, variableNames ?? ArrayUtils.EmptyStrings);
            }
            scope.InterpretedFrame = interpretedFrame;

            // While a file's top level runs, a proc created in it can return from it, as MRI's
            // `proc { return }.call` at the top of a file does (see IsTopLevelReturn).
            var topLevel = scope as RubyTopLevelScope;
            if (topLevel != null) {
                topLevel._activeFlowControlScope = topLevel;
                topLevel.ActiveThreadId = Environment.CurrentManagedThreadId;
            }
        }
        
        [Emitted]
        public static void InitializeScopeNoLocals(RubyScope/*!*/ scope, InterpretedFrame interpretedFrame) {
            scope.InterpretedFrame = interpretedFrame;
        }

        /// <summary>
        /// An eval's own locals exist from the start, as in any other scope - `if false; a = 1; end'
        /// leaves `a' defined as nil for the next eval through the same binding - so the ones the
        /// string declares are defined up front rather than on their first assignment.
        /// </summary>
        [Emitted]
        public static void InitializeEvalScope(RubyScope/*!*/ scope, string/*!*/[]/*!*/ declaredNames, InterpretedFrame interpretedFrame) {
            scope.InterpretedFrame = interpretedFrame;
            foreach (var name in declaredNames) {
                scope.DeclareLocalVariable(name);
            }
        }

        [Emitted]
        public static void SetDataConstant(RubyScope/*!*/ scope, string/*!*/ dataPath, int dataOffset) {
            Debug.Assert(dataOffset >= 0);
            RubyContext context = scope.RubyContext;
            if (!context.DomainManager.Platform.FileExists(dataPath)) {
                // -e and stdin scripts also compile as TopScopeFactoryKind.Main and can contain
                // __END__. MRI does not define DATA for those; defining it as nil would make
                // defined?(DATA) truthy, so leave the constant undefined instead.
                return;
            }

            RubyFile dataFile = new RubyFile(context, dataPath, IOMode.ReadOnly);
            dataFile.Seek(dataOffset, SeekOrigin.Begin);
            context.ObjectClass.SetConstant("DATA", dataFile);
        }

        [Emitted]
        public static RubyModuleScope/*!*/ CreateModuleScope(MutableTuple locals, string[] variableNames, 
            RubyScope/*!*/ parent, RubyModule/*!*/ module) {

            if (parent.RubyContext != module.Context) {
                throw RubyExceptions.CreateTypeError("Cannot open a module '{0}' defined in a foreign runtime #{1}", module.Name, module.Context.RuntimeId);
            }

            RubyModuleScope scope = new RubyModuleScope(parent, module);
            scope.SetDebugName((module.IsClass ? "class" : "module") + " " + module.Name);
            scope.SetLocals(locals, variableNames ?? ArrayUtils.EmptyStrings);
            return scope;
        }

        [Emitted]
        public static RubyMethodScope/*!*/ CreateMethodScope(MutableTuple locals, string[] variableNames, int visibleParameterCount,
            RubyScope/*!*/ parentScope, RubyModule/*!*/ declaringModule, string/*!*/ definitionName, 
            object selfObject, Proc blockParameter, InterpretedFrame interpretedFrame) {

            // Method entry is a safe point, as in MRI: a thread that recurses or calls methods in a
            // loop is reachable by Thread#raise / Thread#kill even where no back edge is.
            RubyUtils.SafePoint();

            var scope = new RubyMethodScope(
                locals, variableNames ?? ArrayUtils.EmptyStrings, visibleParameterCount,
                parentScope, declaringModule, definitionName, selfObject, blockParameter,
                interpretedFrame
            );

            if (_aliasCallSeen) {
                string callee = _pendingCallee;
                if (callee != null) {
                    _pendingCallee = null;
                    if (_pendingCalleeDefinition == definitionName) {
                        scope.CalleeName = callee;
                    }
                }
            }

            if ((TracePoint.ActiveEvents & (int)(TraceEvents.Call | TraceEvents.CCall)) != 0) {
                TracePoint.OnMethodCall(scope);
            }
            return scope;
        }

        #region Light method frames

        // A method whose body never needs its scope - yield and return over self, its parameters, its
        // locals and literals (MethodDefinition.TransformLightBody) - makes none on a call. Its flow-control
        // frame is the literal block it was called with, converted as a library method's is
        // (CreateRfcForMethod), or the shared inactive frame; the scope is made only when something asks
        // for one: a TracePoint hook, or the exception filter. That is the 160-byte RubyMethodScope every
        // other call of a Ruby method allocates.

        /// <summary>
        /// The entry of a light-frame method: the safe point CreateMethodScope makes, then the call's
        /// flow-control frame.
        /// </summary>
        [Emitted]
        public static RuntimeFlowControl/*!*/ EnterLightMethod(Proc blockParameter) {
            RubyUtils.SafePoint();
            return CreateRfcForMethod(blockParameter);
        }

        /// <summary>
        /// The name the method was called by when that was an alias (CreateMethodScope keeps it in the
        /// scope; a light frame keeps it for the scope it may make). Taken either way, as CreateMethodScope does.
        /// </summary>
        [Emitted]
        public static string TakePendingCallee(string/*!*/ definitionName) {
            if (_aliasCallSeen) {
                string callee = _pendingCallee;
                if (callee != null) {
                    _pendingCallee = null;
                    if (_pendingCalleeDefinition == definitionName) {
                        return callee;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The scope of a light-frame method made at its entry because a TracePoint listens for calls, which
        /// it is then reported to; null otherwise.
        /// </summary>
        [Emitted]
        public static RubyMethodScope CreateMethodScopeIfTraced(MethodFrameShape/*!*/ shape, RubyScope/*!*/ parentScope,
            RubyModule/*!*/ declaringModule, MutableTuple locals, object selfObject, Proc blockParameter,
            InterpretedFrame interpretedFrame, RuntimeFlowControl/*!*/ rfc, string calleeName) {

            if ((TracePoint.ActiveEvents & (int)(TraceEvents.Call | TraceEvents.CCall)) != 0) {
                var scope = CreateMethodScopeLazy(shape, parentScope, declaringModule, locals, selfObject, blockParameter,
                    interpretedFrame, rfc, calleeName);
                TracePoint.OnMethodCall(scope);
                return scope;
            }
            return null;
        }

        /// <summary>
        /// The scope of a light-frame method, made when something asks for it (see the RubyMethodScope constructor).
        /// </summary>
        [Emitted]
        public static RubyMethodScope/*!*/ CreateMethodScopeLazy(MethodFrameShape/*!*/ shape, RubyScope/*!*/ parentScope,
            RubyModule/*!*/ declaringModule, MutableTuple locals, object selfObject, Proc blockParameter,
            InterpretedFrame interpretedFrame, RuntimeFlowControl/*!*/ rfc, string calleeName) {

            return new RubyMethodScope(shape, parentScope, declaringModule, locals, selfObject, blockParameter,
                interpretedFrame, rfc, calleeName);
        }

        /// <summary>
        /// MethodYield of a light-frame method: the frame is the scope's if one was made, the call's otherwise.
        /// </summary>
        [Emitted]
        public static bool LightMethodYield(RuntimeFlowControl/*!*/ rfc, RubyScope scope, BlockParam/*!*/ yieldedBlockFlowControl, object returnValue) {
            if (yieldedBlockFlowControl.ReturnReason == BlockReturnReason.Undefined) {
                return false;
            }
            return MethodYieldRfc((scope != null) ? scope.FlowControlScope : rfc, yieldedBlockFlowControl, returnValue);
        }

        /// <summary>
        /// LeaveMethodFrame of a light-frame method: the call's frame, and the scope if one was made.
        /// </summary>
        [Emitted]
        public static void LeaveLightMethodFrame(RuntimeFlowControl/*!*/ rfc, RubyScope scope) {
            LeaveMethodFrame(rfc);
            if (scope != null) {
                if (scope.IsActiveMethod) {
                    scope.LeaveMethod();
                }
                ReleaseScopeFrame(scope);
            }
        }

        #endregion

        // The name an aliased method was called by, handed from the call site to the scope the
        // method body creates first thing (MRI keeps it in the frame for __callee__).
        [ThreadStatic]
        private static string _pendingCallee;
        [ThreadStatic]
        private static string _pendingCalleeDefinition;
        private static bool _aliasCallSeen;

        /// <summary>
        /// Wraps the last argument of a call to a method through an alias, so that the name is
        /// recorded after every argument has been evaluated, just before the method body runs.
        /// </summary>
        [Emitted]
        public static object MarkAliasCall(object lastArgument, string/*!*/ calleeName, string/*!*/ definitionName) {
            _aliasCallSeen = true;
            _pendingCallee = calleeName;
            _pendingCalleeDefinition = definitionName;
            return lastArgument;
        }

        [Emitted]
        public static RubyScope/*!*/ CreateFileInitializerScope(MutableTuple locals, string[] variableNames, RubyScope/*!*/ parent) {
            return new RubyFileInitializerScope(locals, variableNames ?? ArrayUtils.EmptyStrings, parent);
        }

        [Emitted]
        public static RubyBlockScope/*!*/ CreateBlockScope(MutableTuple locals, string[] variableNames, 
            BlockParam/*!*/ blockParam, object selfObject, InterpretedFrame interpretedFrame) {

            // Block entry too: `n.times { }` and `each { }` loop in C#, where no back edge is emitted.
            RubyUtils.SafePoint();

            var scope = new RubyBlockScope(locals, variableNames ?? ArrayUtils.EmptyStrings, blockParam, selfObject, interpretedFrame);
            if ((TracePoint.ActiveEvents & (int)(TraceEvents.BCall | TraceEvents.Call)) != 0) {
                TracePoint.OnBlockCall(scope);
            }
            return scope;
        }

        /// <summary>
        /// The entry of a block whose body never uses its scope (see BlockDefinition.IsScopeFreeBody):
        /// the safe point, and the scope only if a TracePoint wants the call event. Null otherwise; a
        /// TracePoint hook enabled later makes it then (<see cref="CreateBlockScopeLazy"/>).
        /// </summary>
        [Emitted]
        public static RubyBlockScope CreateBlockScopeIfTraced(MutableTuple locals, string[] variableNames,
            BlockParam/*!*/ blockParam, object selfObject, InterpretedFrame interpretedFrame) {

            RubyUtils.SafePoint();
            if ((TracePoint.ActiveEvents & (int)(TraceEvents.BCall | TraceEvents.Call)) != 0) {
                var scope = new RubyBlockScope(locals, variableNames ?? ArrayUtils.EmptyStrings, blockParam, selfObject, interpretedFrame);
                TracePoint.OnBlockCall(scope);
                return scope;
            }
            return null;
        }

        /// <summary>
        /// The first step of the entry of a block whose body needs its scope but does not define a closure
        /// over it: the safe point, then the scope the last finished call through this BlockParam left there
        /// (each, map, times, a yield in a loop; see BlockParam.ParkScope) unless something captured it, or null.
        /// The scope comes off the BlockParam here, after the safe point (which can run a trap handler or a
        /// finalizer) and together with its locals tuple: a call that starts in between finds nothing kept and
        /// makes a scope and tuple of its own, and a call that does not finish normally keeps nothing.
        /// The block reuses the scope's tuple (see <see cref="GetReusableLocals"/>) and hands the scope to
        /// <see cref="CreateReusableBlockScope"/>; <see cref="LeaveReusableBlockScope"/> parks it again.
        /// </summary>
        [Emitted, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RubyBlockScope TakeReusableBlockScope(BlockParam/*!*/ blockParam) {
            // Block entry too: `n.times { }` and `each { }` loop in C#, where no back edge is emitted.
            RubyUtils.SafePoint();

            if (blockParam.MayHaveReusableScope) {
                var scope = blockParam.ReusableScope;
                if (scope != null && !scope.IsCaptured) {
                    blockParam.TakeReusableScope();
                    return scope;
                }
            }
            return null;
        }

        /// <summary>
        /// The locals tuple of a scope <see cref="TakeReusableBlockScope"/> took, for the block to reuse (the
        /// last call cleared it); null if it took none.
        /// </summary>
        [Emitted, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MutableTuple GetReusableLocals(RubyBlockScope reusable) {
            return (reusable != null) ? reusable._locals : null;
        }

        /// <summary>
        /// The scope of a block call that <see cref="TakeReusableBlockScope"/> began: the scope it took, made the
        /// scope of this call, or a new one.
        /// </summary>
        [Emitted, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RubyBlockScope/*!*/ CreateReusableBlockScope(MutableTuple locals, string[] variableNames,
            BlockParam/*!*/ blockParam, object selfObject, InterpretedFrame interpretedFrame, RubyBlockScope reusable) {

            RubyBlockScope scope;
            if (reusable != null) {
                scope = reusable;
                scope.Reinitialize(locals, blockParam, selfObject, interpretedFrame);
            } else {
                scope = new RubyBlockScope(locals, variableNames ?? ArrayUtils.EmptyStrings, blockParam, selfObject, interpretedFrame);
            }

            if ((TracePoint.ActiveEvents & (int)(TraceEvents.BCall | TraceEvents.Call)) != 0) {
                scope.MarkCaptured();
                TracePoint.OnBlockCall(scope);
            }
            return scope;
        }

        /// <summary>
        /// The normal exit of a block that made its scope by <see cref="CreateReusableBlockScope"/>.
        /// An exception leaving the body skips this, and the scope is simply not reused. True if the
        /// scope was kept on the BlockParam, with its locals tuple when <paramref name="keepLocals"/>: the
        /// block then clears the tuple, so that the next call finds its locals nil. The first call through
        /// a BlockParam keeps nothing (see BlockParam.ParkScope): a block called once pays no more than a flag.
        /// </summary>
        [Emitted, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool LeaveReusableBlockScope(RubyBlockScope/*!*/ scope, bool keepLocals) {
            return scope.BlockFlowControl.ParkScope(scope);
        }

        [Emitted]
        public static RubyBlockScope/*!*/ CreateBlockScopeLazy(MutableTuple locals, string[] variableNames,
            BlockParam/*!*/ blockParam, object selfObject, InterpretedFrame interpretedFrame) {
            return new RubyBlockScope(locals, variableNames ?? ArrayUtils.EmptyStrings, blockParam, selfObject, interpretedFrame);
        }

        // TracePoint hooks; emitted code only calls them when TracePoint.ActiveEvents has the event.

        // Emitted behind a test of TracePoint.ActiveEvents in front of every statement: kept out of
        // line, the never-taken branch is one call; inlined, it was a few hundred bytes of machine
        // code per statement in every compiled method body.
        [Emitted, MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceLineEvent(RubyScope scope, string path, int line) {
            // objspace's allocation tracing forces this event on and takes the allocation site of
            // every object created by the statement from here.
            if (ObjectTracking.IsTracingAllocations) {
                ObjectTracking.SetCurrentLine(scope, path, line);
            }
            TracePoint.OnLine(scope, path, line);
        }

        [Emitted]
        public static void TraceReturnEvent(RubyScope scope, object value, string path, int line) {
            TracePoint.OnReturn(scope, value, path, line);
        }

        [Emitted]
        public static void TraceClassEvent(RubyScope scope, string path, int line) {
            if ((TracePoint.ActiveEvents & (int)TraceEvents.Class) != 0) {
                TracePoint.OnClass(scope, path, line);
            }
        }

        [Emitted]
        public static void PrintInteractiveResult(RubyScope/*!*/ scope, MutableString/*!*/ value) {
            var writer = scope.RubyContext.DomainManager.SharedIO.OutputStream;
            writer.WriteByte((byte)'=');
            writer.WriteByte((byte)'>');
            writer.WriteByte((byte)' ');
            var bytes = value.ToByteArray();
            writer.Write(bytes, 0, bytes.Length);
            writer.WriteByte((byte)'\r');
            writer.WriteByte((byte)'\n');
        }

        [Emitted]
        public static object GetLocalVariable(RubyScope/*!*/ scope, string/*!*/ name) {
            return scope.ResolveLocalVariable(name);
        }

        [Emitted]
        public static object SetLocalVariable(object value, RubyScope/*!*/ scope, string/*!*/ name) {
            return scope.ResolveAndSetLocalVariable(name, value);
        }

        [Emitted]
        public static VersionHandle/*!*/ GetSelfClassVersionHandle(RubyScope/*!*/ scope) {
            return scope.SelfImmediateClass.Version;
        }

        /// <summary>
        /// Emitted into the guard of any call-site rule whose target class has been refined.  The rule is
        /// valid exactly while the calling scope's lexical refinement activation is the same instance it
        /// was bound against.
        /// </summary>
        [Emitted]
        public static RefinementActivation/*!*/ GetActiveRefinements(RubyScope/*!*/ scope) {
            return scope.GetActiveRefinements();
        }

        /// <summary>
        /// The same for the guard of a call-site rule, which is only built once a refinement exists: in a
        /// runtime where one ever has, GetActiveRefinements' first test is always false.
        /// </summary>
        [Emitted]
        public static RefinementActivation/*!*/ GetLexicalRefinements(RubyScope/*!*/ scope) {
            return scope.GetLexicalRefinements();
        }

        // The implicit calls a conversion site makes carry no scope, so they cannot see refinements.
        // The two below run ahead of such a site where MRI honours the refinements of the lexical
        // position - "#{x}" and &x - and answer null (use the site) unless one is actually in play.
        // Unless some refinement has the method in question they cost a field read and a hash lookup.

        /// <summary>
        /// "#{x}" where a refinement of x's class defines #to_s.
        /// </summary>
        [Emitted]
        public static MutableString TryConvertToSWithRefinements(RubyScope/*!*/ scope, object value) {
            var context = scope.RubyContext;
            if (!context.HasRefinements || value is MutableString || !context.IsRefinedMethodName("to_s")) {
                return null;
            }
            if (!IsRefinedHere(scope, value, "to_s")) {
                return null;
            }
            var site = context.GetOrCreateSendSite<Func<CallSite, RubyScope, object, object>>(
                "to_s", new RubyCallSignature(0, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );
            return ToSDefaultConversion(context, value, site.Target(site, scope, value));
        }

        /// <summary>
        /// A block argument (&x) in a scope where refinements are active: a Symbol's proc calls the method
        /// with the refinements of this place (MRI's refinement-aware symbol proc), and a refined #to_proc
        /// is the one that converts.
        /// </summary>
        [Emitted]
        public static Proc TryConvertBlockWithRefinements(RubyScope/*!*/ scope, object value) {
            var context = scope.RubyContext;
            if (!context.HasRefinements || value == null || value is Proc) {
                return null;
            }
            // Only a refinement with #to_proc, or - for a Symbol - with the method the proc calls, makes a
            // difference to what &x passes.
            var symbol = value as RubySymbol;
            bool toProcRefined = context.IsRefinedMethodName(Symbols.ToProc);
            if (!toProcRefined && (symbol == null || !context.IsRefinedMethodName(symbol.ToString()))) {
                return null;
            }
            if (scope.GetActiveRefinements().IsEmpty) {
                return null;
            }
            if (!toProcRefined || !IsRefinedHere(scope, value, Symbols.ToProc)) {
                // Symbol#to_proc, but with the calls it makes bound to this scope:
                return (symbol != null) ? Proc.CreateMethodInvoker(scope, symbol.ToString()) : null;
            }
            var site = context.GetOrCreateSendSite<Func<CallSite, RubyScope, object, object>>(
                Symbols.ToProc, new RubyCallSignature(0, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );
            return ToProcValidator(context.GetClassName(value), site.Target(site, scope, value));
        }

        // True if a refinement active in scope supplies value's method of that name.
        private static bool IsRefinedHere(RubyScope/*!*/ scope, object value, string/*!*/ name) {
            var context = scope.RubyContext;
            if (scope.GetActiveRefinements().IsEmpty) {
                return false;
            }
            var method = context.ResolveMethodWithRefinements(value, name, VisibilityContext.AllVisible, scope);
            return method.Found && method.Info.DeclaringModule.IsRefinement;
        }

        #endregion

        #region Context

        [Emitted]
        public static RubyContext/*!*/ GetContextFromModule(RubyModule/*!*/ module) {
            return module.Context;
        }
        
        [Emitted]
        public static RubyContext/*!*/ GetContextFromIRubyObject(IRubyObject/*!*/ obj) {
            return obj.ImmediateClass.Context;
        }
        
        [Emitted]
        public static RubyContext/*!*/ GetContextFromScope(RubyScope/*!*/ scope) {
            return scope.RubyContext;
        }

        [Emitted]
        public static RubyContext/*!*/ GetContextFromMethod(RubyMethod/*!*/ method) {
            return method.Info.Context;
        }

        [Emitted]
        public static RubyContext/*!*/ GetContextFromBlockParam(BlockParam/*!*/ block) {
            return block.RubyContext;
        }

        [Emitted]
        public static RubyContext/*!*/ GetContextFromProc(Proc/*!*/ proc) {
            return proc.LocalScope.RubyContext;
        }

        [Emitted]
        public static Scope/*!*/ GetGlobalScopeFromScope(RubyScope/*!*/ scope) {
            return scope.GlobalScope.Scope;
        }

        #endregion

        #region Blocks

        [Emitted]
        public static Proc InstantiateBlock(RubyScope/*!*/ scope, object self, BlockDispatcher/*!*/ dispatcher) {
            return dispatcher.HasMethod ? new Proc(ProcKind.Block, self, scope, dispatcher) : null;
        }
        [Emitted]
        public static Proc InstantiateLambda(RubyScope/*!*/ scope, object self, BlockDispatcher/*!*/ dispatcher) {
            return dispatcher.HasMethod ? new Proc(ProcKind.Lambda, self, scope, dispatcher) : null;
        }

        [Emitted]
        public static Proc/*!*/ DefineBlock(RubyScope/*!*/ scope, object self, BlockDispatcher/*!*/ dispatcher, object/*!*/ clrMethod) {
            return new Proc(ProcKind.Block, self, scope, dispatcher.SetMethod(clrMethod));
        }

        [Emitted]
        public static Proc/*!*/ DefineLambda(RubyScope/*!*/ scope, object self, BlockDispatcher/*!*/ dispatcher, object/*!*/ clrMethod) {
            return new Proc(ProcKind.Lambda, self, scope, dispatcher.SetMethod(clrMethod));
        }

        /// <summary>
        /// Used in a method call with a block to reset proc-kind when the call is retried
        /// </summary>
        [Emitted]
        public static void InitializeBlock(Proc/*!*/ proc) {
            Assert.NotNull(proc);
            proc.Kind = ProcKind.Block;
        }

        /// <summary>
        /// Implements END block - like if it was a call to at_exit { ... } library method.
        /// </summary>
        [Emitted]
        public static void RegisterShutdownHandler(Proc/*!*/ proc) {
            proc.LocalScope.RubyContext.RegisterShutdownHandler(proc);
        }

        #endregion

        #region Yield: TODO: generate

        [Emitted] 
        public static object Yield0(Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 0);
            try {
                result = proc.Dispatcher.Invoke(blockParam, self, procArg);
            } catch(EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        /// <summary>
        /// A lambda checks the number of arguments wherever it is called, including when it is
        /// the block a method yields to. A plain block does not.
        /// </summary>
        public static void RequireLambdaArity(Proc/*!*/ proc, int argCount) {
            RequireLambdaArity(proc, argCount, null);
        }

        /// <summary>
        /// <paramref name="lastArg"/> is the last of the <paramref name="argCount"/> arguments. A
        /// block whose keywords were lowered onto positional parameters cannot tell a keyword hash
        /// from a positional one by the count alone, so its declared signature decides: MRI counts
        /// only the positional arguments against a lambda, and `|**nil|` refuses keywords in a
        /// proc as well as in a lambda.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RequireLambdaArity(Proc/*!*/ proc, int argCount, object lastArg) {
            // A plain block takes any arguments - the common case, so it does not make the call:
            // only a lambda, or a signature with keywords or **nil, has anything to check.
            if (proc.Kind == ProcKind.Lambda || proc.Dispatcher.ChecksKeywords) {
                CheckLambdaArity(proc, argCount, lastArg);
            }
        }

        private static void CheckLambdaArity(Proc/*!*/ proc, int argCount, object lastArg) {
            var signature = proc.Dispatcher.ParameterSignature;
            // A symbol proc reports [[:req], [:rest]] but checks for its receiver itself
            // ("no receiver given").
            if (signature != null && proc.SymbolName == null) {
                Hash keywords = null;
                // = signature.TakesKeywords || signature.RefusesKeywords, cached
                if (proc.Dispatcher.ChecksKeywords) {
                    keywords = argCount > 0 ? lastArg as Hash : null;
                    if (keywords != null && !keywords.IsKeywordArguments) {
                        keywords = null;
                    }
                    if (keywords != null && keywords.Count > 0 && signature.RefusesKeywords) {
                        throw RubyExceptions.CreateArgumentError("no keywords accepted");
                    }
                }
                if (proc.Kind != ProcKind.Lambda) {
                    return;
                }
                // The signature knows the positional bounds exactly ("1..2" for |a, b = 1|), where
                // the arity alone would only say "1+".
                int positional = argCount - (keywords != null ? 1 : 0);
                int min = signature.MinPositionalCount, max = signature.MaxPositionalCount;
                if (positional < min || (max >= 0 && positional > max)) {
                    throw MakeWrongNumberOfArgumentsErrorN(positional, signature.DescribeExpectedArguments());
                }
                return;
            }

            if (proc.Kind != ProcKind.Lambda) {
                return;
            }

            int arity = proc.Dispatcher.Arity;
            if (argCount == arity) {
                return;
            }

            if (arity >= 0) {
                throw MakeWrongNumberOfArgumentsError(argCount, arity);
            }
            if (argCount < -arity - 1) {
                // MRI spells an unbounded arity "expected 1+".
                throw MakeWrongNumberOfArgumentsErrorN(argCount,
                    (-arity - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "+");
            }
        }

        [Emitted]
        public static object Yield1(object arg1, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 1, arg1);
            try {
                // a lambda takes the single argument as it is: no auto-splatting
                result = proc.Kind == ProcKind.Lambda
                    ? proc.Dispatcher.InvokeNoAutoSplat(blockParam, self, procArg, arg1)
                    : proc.Dispatcher.Invoke(blockParam, self, procArg, arg1);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        // YieldNoAutoSplat1 uses InvokeNoAutoSplat instead of Invoke (used by Call1)
        internal static object YieldNoAutoSplat1(object arg1, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            try {
                result = proc.Dispatcher.InvokeNoAutoSplat(blockParam, self, procArg, arg1);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object Yield2(object arg1, object arg2, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 2, arg2);
            try {
                result = proc.Dispatcher.Invoke(blockParam, self, procArg, arg1, arg2);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object Yield3(object arg1, object arg2, object arg3, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 3, arg3);
            try {
                result = proc.Dispatcher.Invoke(blockParam, self, procArg, arg1, arg2, arg3);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object Yield4(object arg1, object arg2, object arg3, object arg4, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 4, arg4);
            try {
                result = proc.Dispatcher.Invoke(blockParam, self, procArg, arg1, arg2, arg3, arg4);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object YieldN(object[]/*!*/ args, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            Debug.Assert(args.Length > BlockDispatcher.MaxBlockArity);

            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, args.Length, args[args.Length - 1]);
            try {
                result = proc.Dispatcher.Invoke(blockParam, self, procArg, args);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        internal static object Yield(object[]/*!*/ args, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            switch (args.Length) {
                case 0: return RubyOps.Yield0(procArg, self, blockParam);
                case 1: return RubyOps.Yield1(args[0], procArg, self, blockParam);
                case 2: return RubyOps.Yield2(args[0], args[1], procArg, self, blockParam);
                case 3: return RubyOps.Yield3(args[0], args[1], args[2], procArg, self, blockParam);
                case 4: return RubyOps.Yield4(args[0], args[1], args[2], args[3], procArg, self, blockParam);
                default: return RubyOps.YieldN(args, procArg, self, blockParam); 
            }
        }

        [Emitted]
        public static object YieldSplat0(IList/*!*/ splattee, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 0 + splattee.Count, splattee.Count > 0 ? splattee[splattee.Count - 1] : null);
            try {
                result = proc.Dispatcher.InvokeSplat(blockParam, self, procArg, splattee);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object YieldSplat1(object arg1, IList/*!*/ splattee, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 1 + splattee.Count, splattee.Count > 0 ? splattee[splattee.Count - 1] : arg1);
            try {
                result = proc.Dispatcher.InvokeSplat(blockParam, self, procArg, arg1, splattee);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object YieldSplat2(object arg1, object arg2, IList/*!*/ splattee, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 2 + splattee.Count, splattee.Count > 0 ? splattee[splattee.Count - 1] : arg2);
            try {
                result = proc.Dispatcher.InvokeSplat(blockParam, self, procArg, arg1, arg2, splattee);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object YieldSplat3(object arg1, object arg2, object arg3, IList/*!*/ splattee, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 3 + splattee.Count, splattee.Count > 0 ? splattee[splattee.Count - 1] : arg3);
            try {
                result = proc.Dispatcher.InvokeSplat(blockParam, self, procArg, arg1, arg2, arg3, splattee);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object YieldSplat4(object arg1, object arg2, object arg3, object arg4, IList/*!*/ splattee, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, 4 + splattee.Count, splattee.Count > 0 ? splattee[splattee.Count - 1] : arg4);
            try {
                result = proc.Dispatcher.InvokeSplat(blockParam, self, procArg, arg1, arg2, arg3, arg4, splattee);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object YieldSplatN(object[]/*!*/ args, IList/*!*/ splattee, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, args.Length + splattee.Count, splattee.Count > 0 ? splattee[splattee.Count - 1] : args[args.Length - 1]);
            try {
                result = proc.Dispatcher.InvokeSplat(blockParam, self, procArg, args, splattee);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        [Emitted]
        public static object YieldSplatNRhs(object[]/*!*/ args, IList/*!*/ splattee, object rhs, Proc procArg, object self, BlockParam/*!*/ blockParam) {
            object result;
            var proc = blockParam.Proc;
            RequireLambdaArity(proc, args.Length + splattee.Count + 1, rhs);
            try {
                result = proc.Dispatcher.InvokeSplatRhs(blockParam, self, procArg, args, splattee, rhs);
            } catch (EvalUnwinder evalUnwinder) {
                result = blockParam.GetUnwinderResult(evalUnwinder);
            }

            return result;
        }

        #endregion

        #region Methods

        [Emitted] // MethodDeclaration:
        public static object DefineMethod(object target, RubyScope/*!*/ scope, RubyMethodBody/*!*/ body) {
            Assert.NotNull(body, scope);

            RubyModule instanceOwner, singletonOwner;
            RubyMemberFlags instanceFlags, singletonFlags;
            bool moduleFunction = false;

            if (body.HasTarget) {
                if (!RubyUtils.CanDefineSingletonMethod(target)) {
                    throw RubyExceptions.CreateTypeError("can't define singleton");
                }

                instanceOwner = null;
                instanceFlags = RubyMemberFlags.Invalid;
                singletonOwner = scope.RubyContext.GetOrCreateSingletonClass(target);
                singletonFlags = RubyMemberFlags.Public;
            } else {
                var attributesScope = scope.GetMethodAttributesDefinitionScope();
                if ((attributesScope.MethodAttributes & RubyMethodAttributes.ModuleFunction) == RubyMethodAttributes.ModuleFunction) {
                    // Singleton module-function's scope points to the instance method's RubyMemberInfo.
                    // This affects:
                    // 1) super call
                    //    Super call is looking for Method.DeclaringModule while searching MRO, which would fail if the singleton module-function
                    //    was in MRO. Since module-function can only be used on module the singleton method could only be on module's singleton.
                    //    Module's singleton is never part of MRO so we are safe.
                    // 2) trace
                    //    Method call trace reports non-singleton module.

                    // The instance method owner is GetMethodDefinitionOwner.
                    // MRI allows to define m.f. on classes but then doesn't work correctly with it.
                    instanceOwner = scope.GetMethodDefinitionOwner();
                    if (instanceOwner.IsClass) {
                        throw RubyExceptions.CreateTypeError("A module function cannot be defined on a class.");
                    }

                    instanceFlags = RubyMemberFlags.Private;
                    singletonOwner = instanceOwner.GetOrCreateSingletonClass();
                    singletonFlags = RubyMemberFlags.Public;
                    moduleFunction = true;
                } else {
                    instanceOwner = scope.GetMethodDefinitionOwner();

                    // A `def' inside `1.5.instance_eval { }' lands on the singleton class of the
                    // number, which is as far as MRI lets it get: running the block is fine,
                    // defining a method in it is not.
                    var singleton = instanceOwner as RubyClass;
                    if (singleton != null && singleton.IsSingletonClass) {
                        if (singleton.IsDummySingletonClass) {
                            // `1.instance_eval { def f; end }' - the dummy stands in for a
                            // singleton class the receiver can never have
                            throw RubyExceptions.CreateTypeError("can't define singleton");
                        }
                        RubyUtils.RequireDefinableSingleton(singleton.SingletonClassOf);
                    }

                    instanceFlags = (RubyMemberFlags)RubyUtils.GetSpecialMethodVisibility(attributesScope.Visibility, body.Name);
                    singletonOwner = null;
                    singletonFlags = RubyMemberFlags.Invalid;
                }
            }
            
            RubyMethodInfo instanceMethod = null, singletonMethod = null;

            if (body.Ast.UsesBlock) {
                scope.RubyContext.NoteMethodUsingBlock(body.Name);
            }

            if (instanceOwner != null) {
                var definitionScope = DefinitionScope(scope, instanceOwner);
                if (scope.RubyContext.HasRefinements) {
                    body.SetDefinitionRefinements(instanceOwner, definitionScope.GetActiveRefinements());
                } else {
                    body.SetDefinedWithoutRefinements();
                }
                SetMethod(scope.RubyContext, instanceMethod =
                    new RubyMethodInfo(body, definitionScope, instanceOwner, instanceFlags)
                );
            }

            if (singletonOwner != null) {
                if (scope.RubyContext.HasRefinements) {
                    body.SetDefinitionRefinements(singletonOwner, scope.GetActiveRefinements());
                } else {
                    body.SetDefinedWithoutRefinements();
                }
                SetMethod(scope.RubyContext, singletonMethod =
                    new RubyMethodInfo(body, scope, singletonOwner, singletonFlags)
                );
            }

            if (instanceOwner != null && scope.RubyContext.IsWarningEnabled("performance")) {
                WarnOptimizedMethodRedefinition(scope.RubyContext, instanceOwner, body.Name);
            }

            // the method's scope saves the result => singleton module-function uses instance-method
            var method = instanceMethod ?? singletonMethod;

            // :methods coverage records the definition, so that a method that is never called is
            // still reported (with 0); the body's prologue does the counting.
            var coverage = body.Coverage;
            if (coverage != null && (coverage.State.Modes & CoverageModes.Methods) != 0) {
                var location = body.Ast.Location;
                coverage.AddMethod(body, method.DeclaringModule, body.Name,
                    location.Start.Line, location.Start.Column - 1, location.End.Line, location.End.Column - 1);
            }

            // Ruby 2.1+: def returns the method name as a symbol (enables `private def foo`). Interned before
            // method_added, which interns the name as well: the hook then finds it in the identifier cache.
            object result = scope.RubyContext.StringifyIdentifier(body.Name);

            method.DeclaringModule.MethodAdded(body.Name);

            if (moduleFunction) {
                Debug.Assert(!method.DeclaringModule.IsClass);
                method.DeclaringModule.GetOrCreateSingletonClass().MethodAdded(body.Name);
            }

            return result;
        }

        // MRI's vm_init_redefined_flag: the core methods its instructions inline, by class.
        private static readonly Dictionary<string, string[]>/*!*/ _optimizedMethods = new Dictionary<string, string[]> {
            { "Integer", new[] { "+", "-", "*", "/", "%", "==", "===", "<", "<=", ">", ">=", "[]", "succ", "&", "|" } },
            { "Float", new[] { "+", "-", "*", "/", "%", "==", "===", "<", "<=", ">", ">=" } },
            { "String", new[] { "+", "==", "===", "<<", "length", "size", "empty?", "succ", "=~", "freeze", "-@" } },
            { "Array", new[] { "+", "<<", "[]", "[]=", "length", "size", "empty?", "max", "min", "hash", "pack", "include?" } },
            { "Hash", new[] { "[]", "[]=", "length", "size", "empty?" } },
            { "Symbol", new[] { "==", "===" } },
            { "NilClass", new[] { "===", "nil?" } },
            { "TrueClass", new[] { "===" } },
            { "FalseClass", new[] { "===" } },
            { "Regexp", new[] { "=~" } },
            { "Proc", new[] { "call" } },
            { "BasicObject", new[] { "!", "!=" } },
        };

        /// <summary>
        /// Warning[:performance]: redefining a core method MRI's interpreter optimises is reported.
        /// </summary>
        private static void WarnOptimizedMethodRedefinition(RubyContext/*!*/ context, RubyModule/*!*/ owner, string/*!*/ name) {
            string[] names;
            if (owner.IsClass && owner.Name != null && _optimizedMethods.TryGetValue(owner.Name, out names)
                && Array.IndexOf(names, name) >= 0) {
                context.ReportCategoryWarning("performance",
                    String.Format("Redefining '{0}#{1}' disables interpreter and JIT optimizations", owner.Name, name));
            }
        }

        /// <summary>
        /// The scope a method body is compiled against. Normally the one the `def' was written
        /// in; for a method of a refinement, one that also has the refinement's holder in use, so
        /// that the body sees the other refinements the same module declares. The refine block
        /// had them active while it ran, but that activation goes away with the block, and the
        /// body runs later.
        /// </summary>
        private static RubyScope/*!*/ DefinitionScope(RubyScope/*!*/ scope, RubyModule/*!*/ owner) {
            if (!owner.IsRefinement || owner.RefinementHolder == null) {
                return scope;
            }

            var result = new RubyModuleScope(scope, owner);
            result.ActivateRefinements(owner.RefinementHolder);
            return result;
        }

        private static void SetMethod(RubyContext/*!*/ callerContext, RubyMethodInfo/*!*/ method) {
            var owner = method.DeclaringModule;

            // Do not trigger the add-method event just yet, we need to assign the result into closure before executing any user code.
            // If the method being defined is "method_added" itself, we would call that method before the info gets assigned to the closure.
            owner.SetMethodNoEvent(callerContext, method.DefinitionName, method);

            // expose RubyMethod in the scope (the method is bound to the main singleton instance):
            if (owner.GlobalScope != null) {
                RubyOps.ScopeSetMember(
                    owner.GlobalScope.Scope,
                    method.DefinitionName,
                    new RubyMethod(owner.GlobalScope.MainObject, method, method.DefinitionName)
                );
            }
        }

        [Emitted] // AliasStatement:
        public static void AliasMethod(RubyScope/*!*/ scope, string/*!*/ newName, string/*!*/ oldName) {
            scope.GetMethodDefinitionOwner().AddMethodAlias(newName, oldName);
        }

        [Emitted] // UndefineMethod:
        public static void UndefineMethod(RubyScope/*!*/ scope, string/*!*/ name) {
            RubyModule owner = scope.GetMethodDefinitionOwner();

            if (!owner.ResolveMethod(name, VisibilityContext.AllVisible).Found) {
                throw RubyExceptions.CreateUndefinedMethodError(owner, name);
            }
            owner.UndefineMethod(name);
        }

        #endregion

        #region Modules

        [Emitted]
        public static RubyModule/*!*/ DefineGlobalModule(RubyScope/*!*/ scope, string/*!*/ name, string sourcePath, int sourceLine) {
            return DefineModule(scope, scope.Top.TopModuleOrObject, name, sourcePath, sourceLine);
        }

        [Emitted]
        public static RubyModule/*!*/ DefineNestedModule(RubyScope/*!*/ scope, string/*!*/ name, string sourcePath, int sourceLine) {
            return DefineModule(scope, scope.GetInnerMostModuleForConstantLookup(), name, sourcePath, sourceLine);
        }

        [Emitted]
        public static RubyModule/*!*/ DefineModule(RubyScope/*!*/ scope, object target, string/*!*/ name, string sourcePath, int sourceLine) {
            var owner = RubyUtils.GetModuleFromObject(scope, target);
            CheckConstantVisibility(owner, name);
            return DefineModule(scope, owner, name, sourcePath, sourceLine);
        }

        /// <summary>
        /// `module Owner::Name` and `class Owner::Name` name the constant explicitly, so a
        /// private one is out of reach here just as it is for a reference.
        /// </summary>
        private static void CheckConstantVisibility(RubyModule/*!*/ owner, string/*!*/ name) {
            bool isPrivate;
            RubyModule declaringOwner;
            using (owner.Context.ClassHierarchyLocker()) {
                isPrivate = owner.IsPrivateConstantInAncestors(name);
                declaringOwner = isPrivate ? owner.GetConstantOwnerNoLock(name) : null;
            }
            if (isPrivate) {
                RubyContext.SetPrivateConstantReference(declaringOwner ?? owner);
                owner.Context.ResolveMissingConstant(owner, name);
            }
        }

        // thread-safe:
        private static RubyModule/*!*/ DefineModule(RubyScope/*!*/ scope, RubyModule/*!*/ owner, string/*!*/ name,
            string sourcePath, int sourceLine) {
            Assert.NotNull(scope, owner);

            ConstantStorage existing;
            if (owner.TryGetConstant(scope.GlobalScope, name, out existing)) {
                RubyModule module = existing.Value as RubyModule;
                if (module == null || module.IsClass) {
                    throw RubyExceptions.CreateTypeError(DescribePreviousDefinition(owner, name, "{0} is not a module"));
                }
                return module;
            } else {
                // create class/module object:
                var result = owner.Context.DefineModule(owner, name);
                owner.SetConstantLocation(name, sourcePath, sourceLine);
                return result;
            }
        }

        #endregion

        #region Classes

        [Emitted]
        public static RubyClass/*!*/ DefineSingletonClass(RubyScope/*!*/ scope, object obj) {
            RubyUtils.RequireDefinableSingleton(obj);
            var result = scope.RubyContext.GetOrCreateSingletonClass(obj);

            // MRI's rb_singleton_class: a class's singleton class, once exposed, gets a singleton of its
            // own, which descends from the superclass's - so D.singleton_class responds to methods
            // defined in `class << self; class << self` of D's superclass.
            if (obj is RubyClass) {
                result.GetOrCreateSingletonClass();
            }
            return result;
        }

        [Emitted] 
        public static RubyModule/*!*/ DefineGlobalClass(RubyScope/*!*/ scope, string/*!*/ name, object superClassObject,
            string sourcePath, int sourceLine) {
            return DefineClass(scope, scope.Top.TopModuleOrObject, name, superClassObject, sourcePath, sourceLine);
        }

        [Emitted]
        public static RubyModule/*!*/ DefineNestedClass(RubyScope/*!*/ scope, string/*!*/ name, object superClassObject,
            string sourcePath, int sourceLine) {
            return DefineClass(scope, scope.GetInnerMostModuleForConstantLookup(), name, superClassObject, sourcePath, sourceLine);
        }

        [Emitted]
        public static RubyModule/*!*/ DefineClass(RubyScope/*!*/ scope, object target, string/*!*/ name, object superClassObject,
            string sourcePath, int sourceLine) {
            var owner = RubyUtils.GetModuleFromObject(scope, target);
            CheckConstantVisibility(owner, name);
            return DefineClass(scope, owner, name, superClassObject, sourcePath, sourceLine);
        }

        // thread-safe:
        private static RubyClass/*!*/ DefineClass(RubyScope/*!*/ scope, RubyModule/*!*/ owner, string/*!*/ name, object superClassObject,
            string sourcePath, int sourceLine) {
            Assert.NotNull(owner);
            RubyClass superClass = ToSuperClass(owner.Context, superClassObject);

            // only the owner's own constants: a class of that name in a module Object includes is
            // not reopened, a new one is defined (MRI's rb_const_defined_at)
            ConstantStorage existing;
            if (owner.TryGetConstant(scope.GlobalScope, name, out existing)) {

                RubyClass cls = existing.Value as RubyClass;
                if (cls == null || !cls.IsClass) {
                    throw RubyExceptions.CreateTypeError(DescribePreviousDefinition(owner, name, "{0} is not a class"));
                }

                if (superClassObject != null && !ReferenceEquals(cls.SuperClass, superClass)) {
                    throw RubyExceptions.CreateTypeError("superclass mismatch for class {0}", name);
                }
                return cls;
            } else {
                var result = owner.Context.DefineClass(owner, name, superClass, null);
                owner.SetConstantLocation(name, sourcePath, sourceLine);
                return result;
            }
        }

        /// <summary>
        /// MRI points at the existing definition when a class/module name is already taken:
        ///   Foo is not a class
        ///   foo.rb:2: previous definition of Foo was here
        /// </summary>
        private static string/*!*/ DescribePreviousDefinition(RubyModule/*!*/ owner, string/*!*/ name, string/*!*/ format) {
            var message = String.Format(format, name);
            string path;
            int line;
            if (owner.TryGetConstantLocation(name, out path, out line)) {
                message += String.Format("\n{0}:{1}: previous definition of {2} was here", path, line, name);
            }
            return message;
        }

        private static RubyClass/*!*/ ToSuperClass(RubyContext/*!*/ ec, object superClassObject) {
            return (superClassObject != null) ? RubyClass.CheckInheritable(ec, superClassObject) : ec.ObjectClass;
        }

        #endregion

        #region Constants

        /// <summary>
        /// A
        /// ::A
        /// </summary>
        [Emitted]
        public static object GetUnqualifiedConstant(RubyScope/*!*/ scope, ConstantSiteCache/*!*/ cache, string/*!*/ name, bool isGlobal) {
            object result = null;
            RubyModule missingConstantOwner;
            RubyModule owner = null;
            var context = scope.RubyContext;
            using (context.ClassHierarchyLocker()) {
                // Thread safety:
                // Another thread could have already updated the value, so the site version might be the same as CAV.
                // We do the lookup anyways since it is no-op and this only happens rarely.
                //
                // An important invariant holds here: in any time after initialized for the first time the Value field contains a valid value.
                // Threads can read an older value (the previous version) but that is still correct since we don't guarantee immediate
                // propagation of the constant write to all readers.
                // 
                // if (site.Version = CAV) {
                //   <- another thread could increment CAV here - we may return old or new value (both are ok)
                //   value = site.Value;
                // } else {
                //   <- another thread could get here as well and update the site before we get to update it.
                //   GetConstant(...)
                // }

                // Constants might be updated during constant resolution due to autoload. 
                // Any such updates need to invalidate the cache hence we need to capture the version before resolving the constant.
                int newVersion = context.ConstantAccessVersion;

                ConstantStorage storage;
                bool isPrivateGlobal = false;
                if (!isGlobal) {
                    missingConstantOwner = scope.TryResolveConstantNoLock(scope.GlobalScope, name, out storage, out owner);
                } else if (context.ObjectClass.TryResolveConstantNoLock(scope.GlobalScope, name, out storage)) {
                    if (context.ObjectClass.IsPrivateConstant(name)) {
                        // `::NAME' is a qualified reference, which a private constant of Object is out of reach of
                        RubyContext.SetPrivateConstantReference(context.ObjectClass);
                        missingConstantOwner = context.ObjectClass;
                        storage = default(ConstantStorage);
                        // a cached miss would lose the private-constant message
                        isPrivateGlobal = true;
                    } else {
                        missingConstantOwner = null;
                        owner = context.ObjectClass;
                    }
                } else {
                    missingConstantOwner = context.ObjectClass;
                }

                object newCacheValue;
                bool deprecated = false;
                if (missingConstantOwner == null) {
                    result = storage.Value;
                    newCacheValue = MakeConstantCacheValue(context, storage, owner, name, cache.IsCoreLibrary);
                    deprecated = ReportConstantDeprecation(context, owner, name);
                } else {
                    newCacheValue = ConstantSiteCache.WeakMissingConstant;
                }

                if (!context.IsAutoloadInProgress && !deprecated && !isPrivateGlobal && (isGlobal || !IsLexicallyInObjectSingleton(scope))) {
                    cache.Update(newCacheValue, newVersion);
                }
            }

            if (missingConstantOwner != null) {
                result = missingConstantOwner.ConstantMissing(name);
            } else if (context.RactorsStarted && !cache.IsCoreLibrary) {
                RactorIsolation.CheckConstantRead(context, owner, name, result);
            }

            return result;
        }

        /// <summary>
        /// What a constant site caches for a constant's value: its weak reference if it has one.
        /// Once the runtime has started Ractors, a value a non-main Ractor might not be allowed
        /// to see is wrapped, so that a cached read checks (<see cref="ReadRactorCheckedConstant"/>).
        /// </summary>
        private static object/*!*/ MakeConstantCacheValue(RubyContext/*!*/ context, ConstantStorage storage, RubyModule owner, string/*!*/ name,
            bool isCoreLibrary) {
            object cached = storage.WeakValue ?? storage.Value;
            if (context.RactorsStarted && !isCoreLibrary && !RactorIsolation.IsTriviallyShareable(storage.Value)) {
                return new RactorCheckedConstant(context, cached, owner, name);
            }
            return cached;
        }

        /// <summary>
        /// A cached constant read of a value <see cref="MakeConstantCacheValue"/> wrapped: the value,
        /// if the current Ractor may see it.
        /// </summary>
        [Emitted]
        public static object ReadRactorCheckedConstant(object/*!*/ cached) {
            var constant = (RactorCheckedConstant)cached;
            var weak = constant.Value as WeakReference;
            object value = (weak != null) ? weak.Target : constant.Value;
            return RactorIsolation.CheckConstantRead(constant.Context, constant.Owner, constant.Name, value);
        }

        /// <summary>
        /// A1::..::AN
        /// ::A1::..::AN
        /// </summary>
        [Emitted]
        public static object GetQualifiedConstant(RubyScope/*!*/ scope, ConstantSiteCache/*!*/ cache, string/*!*/[]/*!*/ qualifiedName, bool isGlobal) {
            var globalScope = scope.GlobalScope;
            var context = globalScope.Context;

            using (context.ClassHierarchyLocker()) {
                int newVersion = context.ConstantAccessVersion;
                
                ConstantStorage storage;
                bool anyMissing;
                RubyModule topModule = isGlobal ? context.ObjectClass : null;
                RubyModule owner;
                object result = ResolveQualifiedConstant(scope, qualifiedName, topModule, true, out storage, out anyMissing, out owner);
                string name = qualifiedName[qualifiedName.Length - 1];

                // cache result only if no constant was missing:
                if (!anyMissing && !context.IsAutoloadInProgress && (isGlobal || !IsLexicallyInObjectSingleton(scope))) {
                    Debug.Assert(result == storage.Value);
                    cache.Update(MakeConstantCacheValue(context, storage, owner, name, cache.IsCoreLibrary), newVersion);
                }

                return (context.RactorsStarted && !cache.IsCoreLibrary) ? RactorIsolation.CheckConstantRead(context, owner, name, result) : result;
            }
        }

        /// <summary>
        /// {expr}::A1::..::AN
        /// </summary>
        [Emitted]
        public static object GetExpressionQualifiedConstant(object target, RubyScope/*!*/ scope, ExpressionQualifiedConstantSiteCache/*!*/ cache,
            string/*!*/[]/*!*/ qualifiedName) {
            RubyModule module = target as RubyModule;
            if (module == null) {
                throw RubyUtils.CreateNotModuleException(scope, target);
            }

            var condition = cache.Condition;
            RubyContext context = module.Context;

            // Note that the module can be bound to another runtime:
            if (module.Id == condition.ModuleId && context.ConstantAccessVersion == condition.Version) {
                object value = cache.Value;
                if (value.GetType() == typeof(WeakReference)) {
                    return ((WeakReference)value).Target;
                } else if (value.GetType() == typeof(RactorCheckedConstant)) {
                    return ReadRactorCheckedConstant(value);
                } else {
                    return value;
                }
            }

            using (context.ClassHierarchyLocker()) {
                int newVersion = context.ConstantAccessVersion;
                
                ConstantStorage storage;
                bool anyMissing;
                RubyModule owner;
                object result = ResolveQualifiedConstant(scope, qualifiedName, module, true, out storage, out anyMissing, out owner);
                string name = qualifiedName[qualifiedName.Length - 1];

                // cache result only if no constant was missing:
                if (!anyMissing && !context.IsAutoloadInProgress) {
                    Debug.Assert(result == storage.Value);
                    cache.Update(MakeConstantCacheValue(context, storage, owner, name, cache.IsCoreLibrary), newVersion, module);
                }

                return (context.RactorsStarted && !cache.IsCoreLibrary) ? RactorIsolation.CheckConstantRead(context, owner, name, result) : result;
            }
        }

        /// <summary>
        /// Code in `class << obj' runs once per obj when it sits in a loop or a method, and every run
        /// has its own singleton class - and its own classes nested in it - to find constants in,
        /// while a constant site's cache is keyed only on the constant version. Such lookups are not
        /// cached. `class << self' in a class or module body, the common form, keeps the cache.
        /// </summary>
        internal static bool IsLexicallyInObjectSingleton(RubyScope/*!*/ scope) {
            for (RubyScope s = scope; s != null; s = s.Parent) {
                var cls = s.Module as RubyClass;
                if (cls != null && cls.IsSingletonClass && !(cls.SingletonClassOf is RubyModule)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// defined? A
        /// </summary>
        [Emitted]
        public static bool IsDefinedUnqualifiedConstant(RubyScope/*!*/ scope, IsDefinedConstantSiteCache/*!*/ cache, string/*!*/ name) {
            var context = scope.RubyContext;
            using (context.ClassHierarchyLocker()) {
                int newVersion = context.ConstantAccessVersion;
                
                ConstantStorage storage;
                bool exists = scope.TryResolveConstantNoLock(null, name, out storage) == null;
                if (!context.IsAutoloadInProgress && !IsLexicallyInObjectSingleton(scope)) {
                    cache.Update(exists, newVersion);
                }
                return exists;
            }
        }

        /// <summary>
        /// defined? ::A
        /// </summary>
        [Emitted]
        public static bool IsDefinedGlobalConstant(RubyScope/*!*/ scope, IsDefinedConstantSiteCache/*!*/ cache, string/*!*/ name) {
            var context = scope.RubyContext;
            using (context.ClassHierarchyLocker()) {
                int newVersion = context.ConstantAccessVersion;

                ConstantStorage storage;
                bool exists = context.ObjectClass.TryResolveConstantNoLock(null, name, out storage) &&
                    !context.ObjectClass.IsPrivateConstant(name);
                if (!context.IsAutoloadInProgress) {
                    cache.Update(exists, newVersion);
                }
                return exists;
            }
        }

        /// <summary>
        /// defined? A1::..::AN
        /// defined? ::A1::..::AN
        /// </summary>
        [Emitted]
        public static bool IsDefinedQualifiedConstant(RubyScope/*!*/ scope, IsDefinedConstantSiteCache/*!*/ cache,
            string/*!*/[]/*!*/ qualifiedName, bool isGlobal) {

            var context = scope.RubyContext;
            using (context.ClassHierarchyLocker()) {
                int newVersion = context.ConstantAccessVersion;

                ConstantStorage storage;
                bool anyMissing;
                RubyModule topModule = isGlobal ? context.ObjectClass : null;
                RubyModule owner;
                try {
                    owner = ResolveQualifiedConstant(scope, qualifiedName, topModule, false, out storage, out anyMissing, out _) as RubyModule;
                } catch {
                    // autoload can raise an exception
                    scope.RubyContext.SetCurrentException(null);
                    return false;
                }
                
                // Note that the owner could be another runtime's module:
                bool exists = IsVisibleConstantDefined(owner, context, qualifiedName[qualifiedName.Length - 1], out storage);
                
                // cache result only if no constant was missing:
                if (!anyMissing && !context.IsAutoloadInProgress && (isGlobal || !IsLexicallyInObjectSingleton(scope))) {
                    cache.Update(exists, newVersion);
                }

                return exists;
            }
        }

        /// <summary>
        /// defined? {expr}::A
        /// defined? {expr}::A1::..::AN
        /// </summary>
        [Emitted]
        public static bool IsDefinedExpressionQualifiedConstant(object target, RubyScope/*!*/ scope,
            ExpressionQualifiedIsDefinedConstantSiteCache/*!*/ cache, string/*!*/[]/*!*/ qualifiedName) {

            RubyModule module = target as RubyModule;
            if (module == null) {
                return false;
            }

            var condition = cache.Condition;
            RubyContext context = module.Context;

            // Note that the module can be bound to another runtime:
            if (module.Id == condition.ModuleId && context.ConstantAccessVersion == condition.Version) {
                return cache.Value;
            }

            using (context.ClassHierarchyLocker()) {
                int newVersion = context.ConstantAccessVersion;

                ConstantStorage storage;
                bool exists;
                if (qualifiedName.Length == 1) {
                    // Note that the owner could be another runtime's module:
                    exists = IsVisibleConstantDefined(module, context, qualifiedName[0], out storage);
                } else {
                    bool anyMissing;
                    RubyModule owner;
                    try {
                        owner = ResolveQualifiedConstant(scope, qualifiedName, module, false, out storage, out anyMissing, out _) as RubyModule;
                    } catch {
                        // autoload can raise an exception:
                        return false;
                    }

                    // Note that the owner could be another runtime's module:
                    exists = IsVisibleConstantDefined(owner, context, qualifiedName[qualifiedName.Length - 1], out storage);

                    // cache result only if no constant was missing:
                    if (anyMissing) {
                        return exists;
                    } 
                }

                if (!context.IsAutoloadInProgress) {
                    cache.Update(exists, newVersion, module);
                }
                return exists;
            }
        }

        private static object ResolveQualifiedConstant(RubyScope/*!*/ scope, string/*!*/[]/*!*/ qualifiedName, RubyModule topModule, bool isGet,
            out ConstantStorage storage, out bool anyMissing, out RubyModule resultOwner) {

            Debug.Assert(qualifiedName.Length >= 2 || qualifiedName.Length == 1 && isGet);
            RubyContext context = scope.RubyContext;
            context.RequiresClassHierarchyLock();

            RubyModule missingConstantOwner;
            RubyGlobalScope globalScope = scope.GlobalScope;
            int nameCount = (isGet) ? qualifiedName.Length : qualifiedName.Length - 1;

            string name = qualifiedName[0];
            RubyModule firstOwner = null;
            if (topModule == null) {
                // the first name of `A::B` is an ordinary lexical lookup, where a private
                // constant of an enclosing module is perfectly visible
                missingConstantOwner = scope.TryResolveConstantNoLock(globalScope, name, out storage, out firstOwner);
            } else if (TryResolveVisibleConstant(topModule, context, globalScope, name, out storage)) {
                missingConstantOwner = null;
                firstOwner = topModule;
            } else {
                missingConstantOwner = topModule;
            }

            // defined?(A::B::C) never calls const_missing (MRI): a name that is not there makes the
            // whole path undefined. Only the get does what evaluating A::B would.
            object result;
            resultOwner = firstOwner ?? missingConstantOwner;
            if (missingConstantOwner == null) {
                result = storage.Value;
                anyMissing = ReportConstantDeprecation(context, firstOwner, name);
            } else if (!isGet) {
                anyMissing = true;
                RubyContext.ClearPrivateConstantReference();
                return null;
            } else {
                anyMissing = true;
                using (context.ClassHierarchyUnlocker()) {
                    result = missingConstantOwner.ConstantMissing(name);
                }
            }

            for (int i = 1; i < nameCount; i++) {
                RubyModule owner = RubyUtils.GetModuleFromObject(scope, result);
                resultOwner = owner;
                // Note that the owner could be another runtime's module:
                name = qualifiedName[i];
                if (TryResolveVisibleConstant(owner, context, globalScope, name, out storage)) {
                    
                    // Constant write updates constant version in a single runtime only. 
                    // Therefore if the chain mixes modules from different runtimes we cannot cache the result.
                    if (owner.Context != context) {
                        anyMissing = true;
                    }

                    if (ReportConstantDeprecation(context, owner, name)) {
                        anyMissing = true;
                    }

                    result = storage.Value;
                } else if (!isGet) {
                    anyMissing = true;
                    RubyContext.ClearPrivateConstantReference();
                    return null;
                } else {
                    anyMissing = true;
                    using (context.ClassHierarchyUnlocker()) {
                        result = owner.ConstantMissing(name);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Emits the warning Module#deprecate_constant asks for, if the constant that just
        /// resolved is deprecated, and reports whether it did - the caller uses that to leave the
        /// call site uncached, so the warning is not swallowed by the second reference onwards.
        ///
        /// Warning.warn can be overridden in Ruby, so the class hierarchy lock is dropped around
        /// the call exactly as the #const_missing dispatches here do.
        /// </summary>
        private static bool ReportConstantDeprecation(RubyContext/*!*/ context, RubyModule owner, string/*!*/ name) {
            if (!context.HasDeprecatedConstants || owner == null || owner.Context != context) {
                return false;
            }

            RubyModule deprecatedOwner = owner.GetDeprecatedConstantOwnerNoLock(name);
            if (deprecatedOwner == null) {
                return false;
            }

            using (context.ClassHierarchyUnlocker()) {
                context.ReportConstantDeprecation(deprecatedOwner, name);
            }
            return true;
        }

        /// <summary>
        /// A qualified constant reference - `Mod::NAME` - does not see a constant the owner
        /// declared private. MRI reports it as missing, which routes it through #const_missing,
        /// and remembers that it was really there so the default #const_missing can say so.
        /// </summary>
        private static bool TryResolveVisibleConstant(RubyModule/*!*/ owner, RubyContext/*!*/ context, RubyGlobalScope globalScope,
            string/*!*/ name, out ConstantStorage storage) {

            if (!owner.TryResolveConstant(context, globalScope, name, out storage)) {
                return false;
            }

            if (owner.Context != context) {
                return true;
            }

            if (owner.IsPrivateConstantInAncestors(name)) {
                // the NameError names the module that made the constant private, as MRI's does
                RubyContext.SetPrivateConstantReference(owner.GetConstantOwnerNoLock(name) ?? owner);
                storage = default(ConstantStorage);
                return false;
            }

            if (owner.IsTopLevelConstantOnly(name)) {
                storage = default(ConstantStorage);
                return false;
            }
            return true;
        }

        /// <summary>
        /// `defined?(Mod::NAME)` is nil for a constant the owner declared private - and unlike a
        /// reference it must not leave a private-constant reference behind for #const_missing.
        /// </summary>
        private static bool IsVisibleConstantDefined(RubyModule owner, RubyContext/*!*/ context, string/*!*/ name, out ConstantStorage storage) {
            storage = default(ConstantStorage);
            if (owner == null || !owner.TryResolveConstant(context, null, name, out storage)) {
                return false;
            }
            return owner.Context != context
                || (!owner.IsPrivateConstantInAncestors(name) && !owner.IsTopLevelConstantOnly(name));
        }

        [Emitted]
        public static object GetMissingConstant(RubyScope/*!*/ scope, ConstantSiteCache/*!*/ cache, string/*!*/ name) {
            return scope.GetInnerMostModuleForConstantLookup().ConstantMissing(name);
        }

        [Emitted]
        public static object GetGlobalMissingConstant(RubyScope/*!*/ scope, ConstantSiteCache/*!*/ cache, string/*!*/ name) {
            return scope.RubyContext.ObjectClass.ConstantMissing(name);
        }


        [Emitted] // ConstantVariable:
        public static object SetGlobalConstant(object value, RubyScope/*!*/ scope, string/*!*/ name, string sourcePath, int sourceLine,
            RubyEncoding/*!*/ encoding) {
            RubyUtils.SetConstant(scope.RubyContext.ObjectClass, name, value, sourcePath, sourceLine, encoding);
            return value;
        }

        [Emitted] // ConstantVariable:
        public static object SetUnqualifiedConstant(object value, RubyScope/*!*/ scope, string/*!*/ name, string sourcePath, int sourceLine,
            RubyEncoding/*!*/ encoding) {
            RubyUtils.SetConstant(scope.GetInnerMostModuleForConstantLookup(), name, value, sourcePath, sourceLine, encoding);
            return value;
        }

        [Emitted] // ConstantVariable:
        public static object SetQualifiedConstant(object value, object target, RubyScope/*!*/ scope, string/*!*/ name,
            string sourcePath, int sourceLine, RubyEncoding/*!*/ encoding) {
            RubyUtils.SetConstant(RubyUtils.GetModuleFromObject(scope, target), name, value, sourcePath, sourceLine, encoding);
            return value;
        }

        #endregion

        // MakeArray*
        public const int OptimizedOpCallParamCount = 5;
        
        #region MakeArray

        [Emitted]
        public static RubyArray/*!*/ MakeArray0() {
            return new RubyArray(0);
        }

        /// <summary>
        /// Ruby 3 semantics for a call-site `**splat`: the keyword hash is passed as a trailing
        /// positional argument, but an *empty* one is dropped entirely -- `f(1, **{})` calls
        /// `f(1)`, not `f(1, {})`.  A literal `f(1, {})` still passes the hash, which is why this
        /// is only emitted for argument hashes that actually contain a `**` splat.
        /// The result is fed to the ordinary splatting machinery, so it is either [] or [hash].
        /// </summary>
        /// <summary>
        /// `f(k: v, **h)`: h's entries added to the keyword hash being built for the call - the
        /// call's own, so it is filled in place - which is returned; nil adds nothing. Null if h is
        /// anything else: that goes through Hash#merge, for #to_hash.
        /// </summary>
        [Emitted]
        public static object MergeKeywordSplat(RubyScope/*!*/ scope, object keywords, object splatted) {
            if (splatted == null) {
                return keywords;
            }
            var source = splatted as Hash;
            var target = keywords as Hash;
            if (source == null || target == null) {
                return null;
            }
            foreach (var entry in source) {
                RubyUtils.SetHashElement(scope.RubyContext, target, entry.Key, entry.Value);
            }
            return target;
        }

        /// <summary>
        /// <see cref="MergeKeywordSplat"/> for a <paramref name="splatted"/> that has to be a Hash - nil
        /// included, anything else raises TypeError - and a <paramref name="keywords"/> that is one: what
        /// the parameterless super of `def m(k: 1, **rest)` does with rest (KeywordSplatMerge.HashOnly).
        /// </summary>
        [Emitted]
        public static object/*!*/ MergeKeywordHash(RubyScope/*!*/ scope, object keywords, object splatted) {
            if (!(splatted is Hash)) {
                throw RubyExceptions.CreateUnexpectedTypeError(scope.RubyContext, splatted, "Hash");
            }
            return MergeKeywordSplat(scope, keywords, splatted);
        }

        /// <summary>
        /// `f(**h)` with h a Hash or nil: what <see cref="SplatKeywordHash"/> makes of it. Null for
        /// anything else, which the call site first converts with #to_hash (by way of Hash#merge).
        /// </summary>
        [Emitted]
        public static RubyArray SplatKeywordHashIfHash(RubyScope/*!*/ scope, object hash) {
            return (hash == null || hash is Hash) ? SplatKeywordHash(scope, hash) : null;
        }

        [Emitted]
        public static RubyArray/*!*/ SplatKeywordHash(RubyScope/*!*/ scope, object hash) {
            var keywords = KeywordSplatArgument(scope, hash);
            if (keywords == null) {
                return new RubyArray(0);
            }

            var result = new RubyArray(1);
            result.Add(keywords);
            return result;
        }

        /// <summary>
        /// `f(**h)` with h a Hash or nil, as the one argument it passes: null when it passes none at
        /// all (nil, or an empty Hash), and a copy marked as keyword arguments otherwise. A call
        /// site that tests for null and passes the hash plainly (SuperCall) avoids the one-or-zero
        /// element array <see cref="SplatKeywordHash"/> makes and the splatted call it takes.
        /// </summary>
        [Emitted]
        public static Hash KeywordSplatArgument(RubyScope/*!*/ scope, object hash) {
            // `f(**nil)` passes no keywords at all
            if (hash == null) {
                return null;
            }

            var dict = hash as IDictionary<object, object>;
            if (dict == null) {
                throw RubyExceptions.CreateImplicitConversionError(scope.RubyContext.GetClassDisplayName(hash), "Hash");
            }

            if (dict.Count == 0) {
                return null;
            }

            // MRI copies: the callee's **rest must not alias the hash the caller splatted
            var source = dict as Hash;
            var copy = (source != null) ? source.TryCopyAsKeywordArguments(scope.RubyContext.EqualityComparer) : null;
            if (copy == null) {
                copy = new Hash(scope.RubyContext.EqualityComparer, dict.Count);
                foreach (var entry in dict) {
                    copy[entry.Key] = entry.Value;
                }
                copy.IsKeywordArguments = true;
            }
            return copy;
        }

        /// <summary>
        /// The `**rest' a parameterless `super' passes in `def m(a, **rest)' (see KeywordArgumentSplat.PassingOn):
        /// <see cref="KeywordSplatArgument"/> of <paramref name="rest"/>, except that while rest still holds the
        /// very entries the call passed - the usual case - the hash the call passed them in,
        /// <paramref name="keywords"/>, is passed on instead of a copy. That hash is the call's own (see
        /// <see cref="ClearKeywordArguments"/>), and the method has bound its parameters from it and stops
        /// holding it as it passes it on, so the parent may keep it or change it. Not when it is rest itself -
        /// the empty default rest took over (<see cref="MakeKeywordRest"/>), or a hash the method already
        /// passed on.
        /// </summary>
        [Emitted]
        public static Hash PassOnKeywordRest(RubyScope/*!*/ scope, object rest, object keywords) {
            var given = keywords as Hash;
            var hash = rest as Hash;
            if (given != null && hash != null && given != hash && given.IsKeywordArguments && given.HasSameEntries(hash)) {
                return (given.Count > 0) ? given : null;
            }
            return KeywordSplatArgument(scope, rest);
        }

        [Emitted]
        public static RubyArray/*!*/ MakeArray1(object item1) {
            return new RubyArray(new object[] { item1, null, null, null }, 0, 1);
        }

        [Emitted]
        public static RubyArray/*!*/ MakeArray2(object item1, object item2) {
            return new RubyArray(new object[] { item1, item2, null, null }, 0, 2);
        }

        /// <summary>
        /// `[item1, item2].min' / `.max' answered without the array (<see cref="ArrayExtremeSite"/>);
        /// <see cref="IsArrayExtremeNotHandled"/> of the result says the call is to be made as written.
        /// </summary>
        [Emitted]
        public static object ArrayLiteralExtreme(RubyScope/*!*/ scope, object item1, object item2, ArrayExtremeSite/*!*/ site) {
            return site.Invoke(scope, item1, item2);
        }

        [Emitted]
        public static bool IsArrayExtremeNotHandled(object result) {
            return ReferenceEquals(result, ArrayExtremeSite.NotHandled);
        }

        [Emitted]
        public static RubyArray/*!*/ MakeArray3(object item1, object item2, object item3) {
            return new RubyArray(new object[] { item1, item2, item3, null }, 0, 3);
        }

        [Emitted]
        public static RubyArray/*!*/ MakeArray4(object item1, object item2, object item3, object item4) {
            return new RubyArray(new object[] { item1, item2, item3, item4 }, 0, 4);
        }

        [Emitted]
        public static RubyArray/*!*/ MakeArray5(object item1, object item2, object item3, object item4, object item5) {
            return new RubyArray(new object[] { item1, item2, item3, item4, item5 }, 0, 5);
        }

        // The elements of an array literal of more than five, in an array built for it alone
        // (ReflectionCache.MakeArrayOpCall): the Array takes it over instead of copying it.
        [Emitted]
        public static RubyArray/*!*/ MakeArrayOwned(object[]/*!*/ items) {
            Debug.Assert(items.Length >= Utils.MinListSize);
            return new RubyArray(items, 0, items.Length);
        }

        [Emitted]
        public static RubyArray/*!*/ MakeArrayN(object[]/*!*/ items) {
            Debug.Assert(items != null);
            var array = new RubyArray(items.Length);
            array.AddVector(items, 0, items.Length);
            return array;
        }

        #endregion

        #region MakeHash

        [Emitted]
        public static Hash/*!*/ MakeHash0(RubyScope/*!*/ scope) {
            return new Hash(scope.RubyContext.EqualityComparer, 0);
        }
        
        [Emitted]
        public static Hash/*!*/ MakeHash(RubyScope/*!*/ scope, object[]/*!*/ items) {
            return RubyUtils.SetHashElements(scope.RubyContext, new Hash(scope.RubyContext.EqualityComparer, items.Length / 2), items);
        }

        /// <summary>
        /// The trailing hash a call site builds for keyword arguments, marked as such so the
        /// callee can tell `f(a: 1)` from `f({a: 1})`.
        /// </summary>
        [Emitted]
        public static Hash/*!*/ MakeKeywordArgumentsHash(RubyScope/*!*/ scope, object[]/*!*/ items) {
            var result = MakeHash(scope, items);
            result.IsKeywordArguments = true;
            return result;
        }

        [Emitted]
        public static Hash/*!*/ MakeKeywordArgumentsHash0(RubyScope/*!*/ scope) {
            var result = MakeHash0(scope);
            result.IsKeywordArguments = true;
            return result;
        }

        /// <summary>
        /// The default of a method's or block's hidden keyword parameter: the call passed no keywords.
        /// Like <see cref="MakeKeywordArgumentsHash0"/>, and marked as a hash no one else holds
        /// (Hash.IsOmittedKeywords), which a **rest parameter then takes over (<see cref="MakeKeywordRest"/>).
        /// </summary>
        [Emitted]
        public static Hash/*!*/ MakeOmittedKeywordArguments(RubyScope/*!*/ scope) {
            var result = MakeHash0(scope);
            result.IsKeywordArguments = true;
            result.IsOmittedKeywords = true;
            return result;
        }

        // `f(a: x)`, `f(a: x, b: y)` and `f(a: x, b: y, c: z)` with Symbol literals for keys, the
        // usual keyword call: no item array to allocate, and none of the conversions
        // SetHashElement applies to a String or nil key. A repeated key keeps the last value, as
        // in MRI (and SetHashElements).

        [Emitted]
        public static Hash/*!*/ MakeKeywordArgumentsHash1(RubyScope/*!*/ scope, object key1, object value1) {
            var result = Hash.CreateKeywordArguments(scope.RubyContext.EqualityComparer, 1);
            result.AppendUnlinked(key1, value1);
            return result;
        }

        [Emitted]
        public static Hash/*!*/ MakeKeywordArgumentsHash2(RubyScope/*!*/ scope, object key1, object value1, object key2, object value2) {
            var result = Hash.CreateKeywordArguments(scope.RubyContext.EqualityComparer, 2);
            result.AppendUnlinked(key1, value1);
            result.AppendUnlinked(key2, value2);
            return result;
        }

        [Emitted]
        public static Hash/*!*/ MakeKeywordArgumentsHash3(RubyScope/*!*/ scope, object key1, object value1, object key2, object value2,
            object key3, object value3) {
            var result = Hash.CreateKeywordArguments(scope.RubyContext.EqualityComparer, 3);
            result.AppendUnlinked(key1, value1);
            result.AppendUnlinked(key2, value2);
            result.AppendUnlinked(key3, value3);
            return result;
        }

        // `{a: x}`, `{a: x, b: y}` and `{a: x, b: y, c: z}`: a hash literal with Symbol literals for
        // keys, built the way the keyword hashes above are - no item array, none of SetHashElement's
        // String/nil key conversions (a Symbol needs none), and no bucket array until the hash is
        // first probed.

        [Emitted]
        public static Hash/*!*/ MakeSymbolHash1(RubyScope/*!*/ scope, object key1, object value1) {
            var result = Hash.CreateUnlinked(scope.RubyContext.EqualityComparer, 1);
            result.AppendUnlinked(key1, value1);
            return result;
        }

        [Emitted]
        public static Hash/*!*/ MakeSymbolHash2(RubyScope/*!*/ scope, object key1, object value1, object key2, object value2) {
            var result = Hash.CreateUnlinked(scope.RubyContext.EqualityComparer, 2);
            result.AppendUnlinked(key1, value1);
            result.AppendUnlinked(key2, value2);
            return result;
        }

        [Emitted]
        public static Hash/*!*/ MakeSymbolHash3(RubyScope/*!*/ scope, object key1, object value1, object key2, object value2,
            object key3, object value3) {
            var result = Hash.CreateUnlinked(scope.RubyContext.EqualityComparer, 3);
            result.AppendUnlinked(key1, value1);
            result.AppendUnlinked(key2, value2);
            result.AppendUnlinked(key3, value3);
            return result;
        }

        /// <summary>True for the hash a call site built out of keyword syntax.</summary>
        [Emitted]
        public static bool IsKeywordArgumentsHash(object obj) {
            var hash = obj as Hash;
            return hash != null && hash.IsKeywordArguments;
        }

        // The keyword-parameter prologue (see PrismAstBridge.BindKeywordsFrom) reads the hash a
        // keyword call passed with the helpers below rather than with Ruby-level Hash calls. MRI
        // binds keywords without dispatching to Hash methods either, so a redefined Hash#[] or
        // Hash#key? has no say in it. The hash is the one the call site built (or a copy of a
        // splatted one), so it is a plain Hash with the default comparer.

        /// <summary>
        /// True when <paramref name="keywords"/> holds every required keyword - the first
        /// <paramref name="requiredCount"/> of <paramref name="names"/> - and, unless the method
        /// takes **rest, no keyword it does not declare. False means the prologue has an
        /// ArgumentError to word, which it does the slow way.
        /// </summary>
        [Emitted]
        public static bool KeywordArgumentsMatch(object keywords, RubySymbol/*!*/[]/*!*/ names, int requiredCount, bool acceptsRest) {
            var hash = keywords as Hash;
            return hash != null && hash.HasKeywords(names, requiredCount, acceptsRest);
        }

        /// <summary>
        /// The value the call passed for keyword <paramref name="name"/>, or
        /// <see cref="DefaultArgument"/> if it passed none. <paramref name="position"/> is where the
        /// method declares the keyword, which is where a call usually puts it too.
        /// </summary>
        [Emitted]
        public static object GetKeywordArgument(object keywords, RubySymbol/*!*/ name, int position) {
            var hash = keywords as Hash;
            object value;
            return (hash != null && hash.TryGetValueByIdentity(name, position, out value)) ? value : DefaultArgument;
        }

        // A signature that mixes keywords with optional, rest or post positionals is lowered to a
        // single splat that its prologue takes apart (PrismAstBridge.LowerGeneralParameters) with
        // the helpers below rather than with Ruby-level Array calls. The splat is the array the
        // call made for this call - or, for a block that auto-splats an Array argument, a copy of
        // it (see AutoSplatArgument) - so consuming it changes nothing anyone else can see.

        /// <summary>The arguments as they arrived, kept aside for a parameterless `super`.</summary>
        [Emitted]
        public static RubyArray/*!*/ CopySplatArguments(object args) {
            return new RubyArray((RubyArray)args);
        }

        /// <summary>
        /// Takes the hash a keyword call passed off the end of the arguments, or makes an empty one
        /// if the call passed no keywords.
        /// </summary>
        [Emitted]
        public static Hash/*!*/ PopKeywordArguments(RubyScope/*!*/ scope, object args) {
            var list = (RubyArray)args;
            int last = list.Count - 1;
            if (last >= 0 && IsKeywordArgumentsHash(list[last])) {
                return (Hash)list.TakeLast();
            }
            var omitted = MakeHash0(scope);
            omitted.IsOmittedKeywords = true;
            return omitted;
        }

        /// <summary>A block's single Array argument, which it auto-splats.</summary>
        [Emitted]
        public static bool IsAutoSplatArgument(object args) {
            var list = (RubyArray)args;
            return list.Count == 1 && list[0] is RubyArray;
        }

        /// <summary>
        /// The arguments a block that auto-splats its single Array argument binds: a copy of the
        /// array, which the prologue consumes - the caller's array is left as it was.
        /// </summary>
        [Emitted]
        public static RubyArray/*!*/ AutoSplatArgument(object args) {
            return new RubyArray((RubyArray)((RubyArray)args)[0]);
        }

        /// <summary>
        /// The arity check of a method or lambda whose positionals are in the splat. A negative
        /// <paramref name="maximum"/> means a rest parameter took the cap off. <paramref name="requiredKeywords"/>
        /// is the "; required keyword: k" MRI appends.
        /// </summary>
        [Emitted]
        public static void CheckSplatArity(object args, int minimum, int maximum, string/*!*/ requiredKeywords) {
            int count = ((RubyArray)args).Count;
            if (count < minimum || (maximum >= 0 && count > maximum)) {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string expected = (minimum == maximum) ? minimum.ToString(inv)
                    : minimum.ToString(inv) + (maximum < 0 ? "+" : ".." + maximum.ToString(inv));
                throw MakeWrongNumberOfArgumentsErrorN(count, expected + requiredKeywords);
            }
        }

        /// <summary>The next positional argument; nil if there is none left (a block's).</summary>
        [Emitted]
        public static object ShiftSplatArgument(object args) {
            return ((RubyArray)args).TakeFirst();
        }

        /// <summary>
        /// The argument of an optional parameter: the next one if the <paramref name="postCount"/>
        /// post parameters leave one, <see cref="DefaultArgument"/> otherwise.
        /// </summary>
        [Emitted]
        public static object ShiftOptionalSplatArgument(object args, int postCount) {
            var list = (RubyArray)args;
            return (list.Count <= postCount) ? DefaultArgument : list.TakeFirst();
        }

        /// <summary>
        /// What the rest parameter takes: everything the <paramref name="postCount"/> post parameters
        /// leave - nothing if a block was given too few arguments to fill even those.
        /// </summary>
        [Emitted]
        public static RubyArray/*!*/ ShiftRestSplatArguments(object args, int postCount) {
            var list = (RubyArray)args;
            return list.TakeFirst(Math.Max(0, list.Count - postCount));
        }

        /// <summary>
        /// The **rest parameter: a new hash with whatever the call passed beyond the declared
        /// <paramref name="names"/>. It is a copy, so the method may change it freely. When the call
        /// passed no keywords at all, the empty default the method made for itself is that new hash
        /// already: nobody else holds it (Hash.IsOmittedKeywords), so it is taken over, not copied.
        /// </summary>
        [Emitted]
        public static Hash/*!*/ MakeKeywordRest(object keywords, RubySymbol/*!*/[]/*!*/ names) {
            var hash = (Hash)keywords;
            if (hash.IsOmittedKeywords && hash.Count == 0) {
                hash.IsOmittedKeywords = false;
                hash.IsKeywordArguments = false;
                return hash;
            }
            return hash.CopyWithoutKeywords(names);
        }

        /// <summary>
        /// The argument a parameter of a method that declares no keyword parameters is about to
        /// be given. `f(a: 1)` there is a call that passes a positional Hash - Ruby 3 has no
        /// keyword arguments without keyword parameters to receive them - and the callee may keep
        /// that hash and pass it on for the rest of its life. So the mark comes off here, at the
        /// one point where it is known to have done its job; leaving it on is how a hash stored
        /// by `def set_filter(filter, decode_parms = nil)` and handed on later turns into the
        /// keyword arguments of an unrelated call.
        ///
        /// The hash is always one the call site made for this call (see
        /// <see cref="MakeKeywordArgumentsHash"/> and the copies the splat paths take), so it can
        /// be cleared in place. Being one of those, it is a Hash and not an instance of a subclass:
        /// the test is of the exact class, which for anything else is a compare, where `is Hash'
        /// walks the class's base types on every call made with an argument that is not a Hash.
        /// </summary>
        [Emitted, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object ClearKeywordArguments(object obj) {
            if (obj != null && obj.GetType() == typeof(Hash)) {
                var hash = Unsafe.As<Hash>(obj);
                if (hash.IsKeywordArguments) {
                    hash.IsKeywordArguments = false;
                }
            }
            return obj;
        }

        /// <summary>
        /// The array a rest parameter is about to be given. A trailing hash that arrived as the
        /// keyword arguments of this call is not the keyword arguments of anything once it is
        /// sitting in an array, so the mark comes off - otherwise passing the array on would make
        /// them keywords again, which is what Ruby 3 separated. A ruby2_keywords method is the
        /// exception: it gets a hash marked the other way, which a later splat turns back into
        /// keywords. Either way the caller's hash is left alone and a copy is stored.
        /// </summary>
        [Emitted]
        public static RubyArray/*!*/ NormalizeRestArgument(RubyArray/*!*/ args, RubyMethodBody/*!*/ body) {
            return NormalizeRestArgument(args, body.Ruby2Keywords);
        }

        public static RubyArray/*!*/ NormalizeRestArgument(RubyArray/*!*/ args, bool ruby2Keywords) {
            int last = args.Count - 1;
            if (last < 0) {
                return args;
            }

            var hash = args[last] as Hash;
            if (hash == null || !hash.IsKeywordArguments) {
                return args;
            }

            var copy = new Hash(hash);
            copy.IsRuby2KeywordsHash = ruby2Keywords;
            args[last] = copy;
            return args;
        }

        /// <summary>
        /// A `*args' at a call site. A hash a ruby2_keywords method is passing along goes back to
        /// being keyword arguments here - that is the whole point of the mark - as a copy, so that
        /// the array the caller is holding keeps its marked one.
        /// </summary>
        /// <summary>
        /// The same for an argument array that was recorded rather than splatted - what an
        /// Enumerator keeps from the call that made it, and replays later.
        /// </summary>
        public static object[]/*!*/ RestoreRuby2Keywords(object[]/*!*/ args) {
            int last = args.Length - 1;
            if (last < 0) {
                return args;
            }

            var hash = args[last] as Hash;
            if (hash == null || !hash.IsRuby2KeywordsHash) {
                return args;
            }

            var copy = new Hash(hash);
            copy.IsKeywordArguments = true;

            var result = (object[])args.Clone();
            result[last] = copy;
            return result;
        }

        [Emitted]
        public static IList/*!*/ SplatRuby2Keywords(IList/*!*/ list) {
            int last = list.Count - 1;
            if (last < 0) {
                return list;
            }

            var hash = list[last] as Hash;
            if (hash == null || !hash.IsRuby2KeywordsHash) {
                return list;
            }

            var copy = new Hash(hash);
            copy.IsKeywordArguments = true;

            var result = new RubyArray(list.Count);
            for (int i = 0; i < last; i++) {
                result.Add(list[i]);
            }
            result.Add(copy);
            return result;
        }

        #endregion

        #region Array

        [Emitted]
        public static RubyArray/*!*/ AddRange(RubyArray/*!*/ array, IList/*!*/ list) {
            return array.AddRange(list);
        }

        [Emitted] // method call:
        public static RubyArray/*!*/ AddSubRange(RubyArray/*!*/ result, IList/*!*/ array, int start, int count) {
            return result.AddRange(array, start, count);
        }

        [Emitted]
        public static RubyArray/*!*/ AddItem(RubyArray/*!*/ array, object item) {
            array.Add(item);
            return array;
        }

        /// <summary>
        /// `[*list, item]` as a new array of the size it ends up: what `super(*rest, **kw)` passes
        /// splatted when kw is not empty (SuperCall).
        /// </summary>
        [Emitted]
        public static RubyArray/*!*/ SplatAppendItem(IList/*!*/ list, object item) {
            var result = new RubyArray(list.Count + 1);
            result.AddRange(list);
            result.Add(item);
            return result;
        }

        /// <summary>
        /// `[*x]` and `a = *x`. The result is always a new plain Array: MRI copies, so that
        /// neither the splatted array itself nor an Array subclass nor a frozen #to_a result
        /// shows through.
        /// </summary>
        [Emitted]
        public static IList/*!*/ Unsplat(object splattee) {
            var list = splattee as IList;
            if (list == null) {
                var single = new RubyArray(1);
                single.Add(splattee);
                return single;
            }
            return new RubyArray(list);
        }

        // CaseExpression
        [Emitted]
        public static bool ExistsUnsplatCompare(CallSite<Func<CallSite, object, object, object>>/*!*/ comparisonSite, object splattee, object value) {
            var list = splattee as IList;
            if (list != null) {
                for (int i = 0; i < list.Count; i++) {
                    if (IsTrue(comparisonSite.Target(comparisonSite, list[i], value))) {
                        return true;
                    }
                }
                return false;
            } else {
                return IsTrue(comparisonSite.Target(comparisonSite, splattee, value)); 
            }
        }

        // CaseExpression
        [Emitted]
        public static bool ExistsUnsplat(object splattee) {
            var list = splattee as IList;
            if (list != null) {
                for (int i = 0; i < list.Count; i++) {
                    if (IsTrue(list[i])) {
                        return true;
                    }
                }
                return false;
            } else {
                return IsTrue(splattee);
            }
        }

        [Emitted] // parallel assignment:
        public static object GetArrayItem(IList/*!*/ array, int index) {
            Debug.Assert(index >= 0);
            return index < array.Count ? array[index] : null;
        }

        [Emitted] // parallel assignment:
        public static object GetTrailingArrayItem(IList/*!*/ array, int index, int explicitCount) {
            Debug.Assert(index >= 0);
            // Trailing l-values are counted from the end of the RHS, but only once the RHS has
            // enough elements to reach them: `a, b, *c, d = [1]` leaves d nil rather than
            // indexing past the end.
            int i = Math.Max(array.Count, explicitCount) - index;
            return i >= 0 && i < array.Count ? array[i] : null;
        }

        [Emitted] // parallel assignment:
        public static RubyArray/*!*/ GetArrayRange(IList/*!*/ array, int startIndex, int explicitCount) {
            int size = array.Count - explicitCount;
            if (size > 0) {
                RubyArray result = new RubyArray(size);
                for (int i = 0; i < size; i++) {
                    result.Add(array[startIndex + i]);
                }
                return result;
            } else {
                return new RubyArray();
            }
        }

        #endregion

        #region CLR Vectors (factories mimic Ruby Array factories)

        [Emitted, RubyConstructor]
        public static object/*!*/ CreateVector<TElement>(
            ConversionStorage<TElement>/*!*/ elementConversion, 
            ConversionStorage<Union<IList, int>>/*!*/ toAryToInt, 
            BlockParam block, RubyClass/*!*/ self, [NotNull]object/*!*/ arrayOrSize) {

            Debug.Assert(typeof(TElement) == self.GetUnderlyingSystemType().GetElementType());

            var site = toAryToInt.GetSite(CompositeConversionAction.Make(self.Context, CompositeConversion.ToAryToInt));
            var union = site.Target(site, arrayOrSize);

            if (union.First != null) {
                // block ignored
                return CreateVectorInternal(elementConversion, union.First);
            } else if (block != null) {
                return PopulateVector(elementConversion, CreateVectorInternal<TElement>(union.Second), block);
            } else {
                return CreateVectorInternal<TElement>(union.Second);
            }
        }

        [Emitted, RubyConstructor]
        public static Array/*!*/ CreateVectorWithValues<TElement>(ConversionStorage<TElement>/*!*/ elementConversion,
            RubyClass/*!*/ self, [DefaultProtocol]int size, [DefaultProtocol]TElement value) {
            Debug.Assert(typeof(TElement) == self.GetUnderlyingSystemType().GetElementType());

            TElement[] result = CreateVectorInternal<TElement>(size);
            for (int i = 0; i < result.Length; i++) {
                result[i] = value;
            }
            return result;
        }

        private static TElement[]/*!*/ CreateVectorInternal<TElement>(int size) {
            if (size < 0) {
                throw RubyExceptions.CreateArgumentError("negative array size");
            }

            return new TElement[size];
        }

        private static Array/*!*/ CreateVectorInternal<TElement>(ConversionStorage<TElement>/*!*/ elementConversion, IList/*!*/ list) {
            var site = elementConversion.GetDefaultConversionSite();

            var result = new TElement[list.Count];
            for (int i = 0; i < result.Length; i++) {
                object item = list[i];
                result[i] = (item is TElement) ? (TElement)item : site.Target(site, item);
            }

            return result;
        }

        private static object PopulateVector<TElement>(ConversionStorage<TElement>/*!*/ elementConversion, TElement[]/*!*/ array, BlockParam/*!*/ block) {
            var site = elementConversion.GetDefaultConversionSite();

            for (int i = 0; i < array.Length; i++) {
                object item;
                if (block.Yield(i, out item)) {
                    return item;
                }
                array[i] = site.Target(site, item);
            }
            return array;
        }

        #endregion

        #region Global Variables

        [Emitted]
        public static object GetGlobalVariable(RubyScope/*!*/ scope, string/*!*/ name) {
            object value;
            var context = scope.RubyContext;
            if (context.RactorsStarted && TryGetRactorLocalGlobal(context, name, out value)) {
                return value;
            }
            // no error reported if the variable doesn't exist:
            context.TryGetGlobalVariable(scope, name, out value);
            return value;
        }

        /// <summary>
        /// A plain read of a global, which in verbose mode warns when the global was never assigned
        /// (MRI's rb_gvar_undef_getter). `$x ||= v' reads without the warning.
        /// </summary>
        [Emitted]
        public static object ReadGlobalVariable(RubyScope/*!*/ scope, string/*!*/ name) {
            object value;
            var context = scope.RubyContext;
            if (context.RactorsStarted && TryGetRactorLocalGlobal(context, name, out value)) {
                return value;
            }
            if (!context.TryGetGlobalVariable(scope, name, out value) && context.Verbose is bool && (bool)context.Verbose) {
                context.ReportWarning(String.Format("global variable '${0}' not initialized", name), true);
            }
            return value;
        }

        /// <summary>
        /// GetGlobalVariable through the site's cached variable: an ordinary global is one
        /// version check and a field read, with no lookup and no lock.
        /// </summary>
        [Emitted]
        public static object GetCachedGlobalVariable(RubyScope/*!*/ scope, GlobalVariableSite/*!*/ site) {
            var context = scope.RubyContext;
            if (context.RactorsStarted && !site.IsCoreLibrary && RactorIsolation.IsNonMain(context)) {
                return GetGlobalVariable(scope, site.Name);
            }
            var variable = site.GetVariable(context);
            var plain = variable as GlobalVariableInfo;
            if (plain != null) {
                return plain.Value;
            }
            return (variable != null) ? context.GetGlobalVariableValue(variable, scope) : null;
        }

        /// <summary>ReadGlobalVariable through the site's cached variable.</summary>
        [Emitted]
        public static object ReadCachedGlobalVariable(RubyScope/*!*/ scope, GlobalVariableSite/*!*/ site) {
            var context = scope.RubyContext;
            if (context.RactorsStarted && !site.IsCoreLibrary && RactorIsolation.IsNonMain(context)) {
                return ReadGlobalVariable(scope, site.Name);
            }
            var variable = site.GetVariable(context);
            var plain = variable as GlobalVariableInfo;
            if (plain != null) {
                return plain.Value;
            }
            if (variable != null) {
                return context.GetGlobalVariableValue(variable, scope);
            }
            if (context.Verbose is bool && (bool)context.Verbose) {
                context.ReportWarning(String.Format("global variable '${0}' not initialized", site.Name), true);
            }
            return null;
        }

        /// <summary>
        /// SetGlobalVariable through the site's cached variable. An ordinary global is assigned
        /// in place; everything else - a new global, a special or read-only one - takes the
        /// general path, which validates and creates.
        /// </summary>
        [Emitted]
        public static object SetCachedGlobalVariable(object value, RubyScope/*!*/ scope, GlobalVariableSite/*!*/ site) {
            var context = scope.RubyContext;
            if (context.RactorsStarted && !site.IsCoreLibrary && RactorIsolation.IsNonMain(context)) {
                return SetGlobalVariable(value, scope, site.Name);
            }
            var plain = site.GetVariable(context) as GlobalVariableInfo;
            if (plain != null) {
                plain.Assign(value);
                context.GlobalVariableAssigned(scope, site.Name, value);
            } else {
                context.SetGlobalVariable(scope, site.Name, value);
            }
            return value;
        }

        [Emitted]
        public static bool IsDefinedGlobalVariable(RubyScope/*!*/ scope, string/*!*/ name) {
            GlobalVariable variable;
            var context = scope.RubyContext;
            if (context.RactorsStarted) {
                RactorIsolation.CheckGlobalVariable(context, name);
            }
            return context.TryGetGlobalVariable(name, out variable) && variable.IsDefined;
        }

        [Emitted]
        public static object SetGlobalVariable(object value, RubyScope/*!*/ scope, string/*!*/ name) {
            var context = scope.RubyContext;
            if (context.RactorsStarted) {
                var ractor = RactorIsolation.CheckGlobalVariable(context, name);
                if (ractor != null && RactorIsolation.IsDebugVariable(name)) {
                    ractor.Debug = value;
                    return value;
                }
            }
            context.SetGlobalVariable(scope, name, value);
            return value;
        }

        [Emitted]
        public static void AliasGlobalVariable(RubyScope/*!*/ scope, string/*!*/ newName, string/*!*/ oldName) {
            var context = scope.RubyContext;
            if (context.RactorsStarted) {
                RactorIsolation.CheckGlobalVariables(context);
            }
            context.AliasGlobalVariable(newName, oldName);
        }

        /// <summary>
        /// A global read in a runtime that has started Ractors: one a non-main Ractor may not
        /// use raises, and $DEBUG is the Ractor's own. False for the rest, which read as usual.
        /// </summary>
        private static bool TryGetRactorLocalGlobal(RubyContext/*!*/ context, string/*!*/ name, out object value) {
            var ractor = RactorIsolation.CheckGlobalVariable(context, name);
            if (ractor != null && RactorIsolation.IsDebugVariable(name)) {
                value = ractor.Debug;
                return true;
            }
            value = null;
            return false;
        }

        #endregion

        #region DLR Scopes

        internal static bool TryGetGlobalScopeConstant(RubyContext/*!*/ context, Scope/*!*/ scope, string/*!*/ name, out object value) {
            string mangled;
            ScopeStorage scopeStorage = ((object)scope.Storage) as ScopeStorage;
            if (scopeStorage != null) {
                return scopeStorage.TryGetValue(name, false, out value)
                    || (mangled = RubyUtils.TryMangleName(name)) != null && scopeStorage.TryGetValue(mangled, false, out value);
            } else {
                return context.Operations.TryGetMember(scope, name, out value)
                    || (mangled = RubyUtils.TryMangleName(name)) != null && context.Operations.TryGetMember(scope, mangled, out value);
            }
        }

        /// <summary>
        /// Takes back what <see cref="ScopeSetMember"/> published, if the scope still holds that
        /// very object under the name.
        /// </summary>
        internal static void ScopeRemoveMember(Scope/*!*/ scope, string/*!*/ name, object value) {
            var scopeStorage = ((object)scope.Storage) as ScopeStorage;
            if (scopeStorage != null) {
                object current;
                if (scopeStorage.TryGetValue(name, false, out current) && ReferenceEquals(current, value)) {
                    scopeStorage.DeleteValue(name, false);
                }
                return;
            }

            var stringDict = ((object)scope.Storage) as StringDictionaryExpando;
            if (stringDict != null) {
                object current;
                if (stringDict.Dictionary.TryGetValue(name, out current) && ReferenceEquals(current, value)) {
                    stringDict.Dictionary.Remove(name);
                }
            }
        }

        // TODO:
        internal static void ScopeSetMember(Scope scope, string name, object value) {
            object storage = (object)scope.Storage;

            var scopeStorage = storage as ScopeStorage;
            if (scopeStorage != null) {
                scopeStorage.SetValue(name, false, value);
                return;
            }

            var stringDict = storage as StringDictionaryExpando;
            if (stringDict != null) {
                stringDict.Dictionary[name] = value;
                return;
            }
            
            throw new NotImplementedException();
        }

        #endregion

        #region Regex

        [Emitted] //RegexMatchReference:
        public static MutableString GetCurrentMatchGroup(RubyScope/*!*/ scope, int index) {
            Debug.Assert(index >= 0);
            return scope.GetInnerMostClosureScope().GetCurrentMatchGroup(index);
        }

        [Emitted] //RegexMatchReference:
        public static MatchData GetCurrentMatchData(RubyScope/*!*/ scope) {
            return scope.GetInnerMostClosureScope().CurrentMatch;
        }

        [Emitted] //RegexMatchReference:
        public static MutableString GetCurrentMatchLastGroup(RubyScope/*!*/ scope) {
            return scope.GetInnerMostClosureScope().GetCurrentMatchLastGroup();
        }

        [Emitted] //RegexMatchReference:
        public static MutableString GetCurrentPreMatch(RubyScope/*!*/ scope) {
            return scope.GetInnerMostClosureScope().GetCurrentPreMatch();
        }

        [Emitted] //RegexMatchReference:
        public static MutableString GetCurrentPostMatch(RubyScope/*!*/ scope) {
            return scope.GetInnerMostClosureScope().GetCurrentPostMatch();
        }

        [Emitted] //RegularExpression:
        public static bool MatchLastInputLine(RubyRegex/*!*/ regex, RubyScope/*!*/ scope) {
            var str = scope.GetInnerMostClosureScope().LastInputLine as MutableString;
            return (str != null) ? RubyRegex.SetCurrentMatchData(scope, regex, str) != null : false;
        }

        [Emitted] //MatchExpression:
        public static object MatchString(MutableString str, RubyRegex/*!*/ regex, RubyScope/*!*/ scope) {
            var match = RubyRegex.SetCurrentMatchData(scope, regex, str);
            return (match != null) ? ScriptingRuntimeHelpers.Int32ToObject(match.Index) : null;
        }

        #endregion

        /// <summary>
        /// Specialized signatures exist for upto the following number of string parts
        /// </summary>
        public const int MakeStringParamCount = 2;

        #region CreateRegex

        private static RubyRegex/*!*/ CreateRegexWorker(
            RubyRegexOptions options, 
            StrongBox<RubyRegex> regexpCache, 
            bool isLiteralWithoutSubstitutions,
            Func<RubyRegex> createRegex) {

            try {
                bool once = ((options & RubyRegexOptions.Once) == RubyRegexOptions.Once) || isLiteralWithoutSubstitutions;
                if (once) {
                    // Note that the user is responsible for thread synchronization
                    if (regexpCache.Value == null) {
                        regexpCache.Value = CreateFrozen(createRegex);
                    }
                    return regexpCache.Value;
                } else {
                    // In the future, we can consider caching the last Regexp. For some regexp literals 
                    // with substitution, the substition will be the same most of the time
                    return CreateFrozen(createRegex);
                }
            } catch (RegexpError e) {
                if (isLiteralWithoutSubstitutions) {
                    // Ideally, this should be thrown during parsing of the source, even if the 
                    // expression happens to be unreachable at runtime.
                    throw new SyntaxError(e.Message);
                } else {
                    throw;
                }
            }
        }

        /// <summary>
        /// MRI freezes a regexp literal, interpolated or not, but leaves Regexp.new unfrozen.
        /// Every literal is built through here, so this is the one place that has to do it.
        /// It does not intern them: /x/.equal?(/x/) is false in MRI too.
        /// </summary>
        private static RubyRegex/*!*/ CreateFrozen(Func<RubyRegex> createRegex) {
            var result = createRegex();
            result.Freeze();
            return result;
        }

        // A literal without substitutions is built once and cached in its site's box, so every
        // later evaluation only reads the box. The construction is split off: the delegate it
        // makes captures the parameters, and a closure over parameters is allocated on entry to
        // the method that declares them - on every evaluation, even one that never needs it.
        [Emitted]
        public static RubyRegex/*!*/ CreateRegexB(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            return regexpCache.Value ?? CreateRegexBWorker(bytes, encoding, options, regexpCache);
        }

        private static RubyRegex/*!*/ CreateRegexBWorker(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            Func<RubyRegex> createRegex = delegate { return new RubyRegex(CreateMutableStringB(bytes, encoding), options); };
            return CreateRegexWorker(options, regexpCache, true, createRegex);
        }

        [Emitted]
        public static RubyRegex/*!*/ CreateRegexL(string/*!*/ str1, RubyEncoding/*!*/ encoding, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            return regexpCache.Value ?? CreateRegexLWorker(str1, encoding, options, regexpCache);
        }

        private static RubyRegex/*!*/ CreateRegexLWorker(string/*!*/ str1, RubyEncoding/*!*/ encoding, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            Func<RubyRegex> createRegex = delegate { return new RubyRegex(CreateMutableStringL(str1, encoding), options); };
            return CreateRegexWorker(options, regexpCache, true, createRegex);
        }
        
        [Emitted]
        public static RubyRegex/*!*/ CreateRegexM(MutableString str1, RubyEncoding/*!*/ encoding, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            Func<RubyRegex> createRegex = delegate { return new RubyRegex(CreateMutableStringM(str1, encoding), options); };
            return CreateRegexWorker(options, regexpCache, false, createRegex);
        }

        [Emitted]
        public static RubyRegex/*!*/ CreateRegexLM(string/*!*/ str1, MutableString str2, RubyEncoding/*!*/ encoding, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            Func<RubyRegex> createRegex = delegate { return new RubyRegex(CreateMutableStringLM(str1, str2, encoding), options); };
            return CreateRegexWorker(options, regexpCache, false, createRegex);
        }

        [Emitted]
        public static RubyRegex/*!*/ CreateRegexML(MutableString str1, string/*!*/ str2, RubyEncoding/*!*/ encoding, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            Func<RubyRegex> createRegex = delegate { return new RubyRegex(CreateMutableStringML(str1, str2, encoding), options); };
            return CreateRegexWorker(options, regexpCache, false, createRegex);
        }

        [Emitted]
        public static RubyRegex/*!*/ CreateRegexMM(MutableString str1, MutableString str2, RubyEncoding/*!*/ encoding, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            Func<RubyRegex> createRegex = delegate { return new RubyRegex(CreateMutableStringMM(str1, str2, encoding), options); };
            return CreateRegexWorker(options, regexpCache, false, createRegex);
        }

        [Emitted]
        public static RubyRegex/*!*/ CreateRegexN(MutableString[]/*!*/ strings, RubyRegexOptions options, StrongBox<RubyRegex> regexpCache) {
            Func<RubyRegex> createRegex = delegate { return new RubyRegex(CreateMutableStringN(strings), options); };
            return CreateRegexWorker(options, regexpCache, false, createRegex);
        }

        #endregion

        #region CreateMutableString

        [Emitted]
        public static MutableString/*!*/ CreateMutableStringB(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            return MutableString.CreateBinary(bytes, encoding);
        }

        [Emitted]
        public static MutableString/*!*/ CreateMutableStringL(string/*!*/ str1, RubyEncoding/*!*/ encoding) {
            return MutableString.Create(str1, encoding);
        }

        [Emitted]
        public static MutableString/*!*/ CreateMutableStringM(MutableString str1, RubyEncoding/*!*/ encoding) {
            var result = MutableString.CreateInternal(str1, encoding);
            // MRI's "#{x}" is "" + x.to_s: an empty literal in the source encoding, which an
            // ASCII-only x leaves as it is - unless that is US-ASCII, which gives way to any other
            if (str1 != null && result.Encoding != encoding && encoding != RubyEncoding.Ascii && encoding.IsAsciiIdentity &&
                result.IsAscii()) {
                result.ForceEncoding(encoding);
            }
            return result;
        }

        #region frozen string literals

        /// <summary>
        /// MRI's fstring table. Under `# frozen_string_literal: true` two literals with the same
        /// bytes and encoding are the *same* object, not merely two frozen equal ones, so
        /// "foo".equal?("foo") is true. Entries live as long as the runtime, as they do in MRI.
        /// </summary>
        private static readonly ConcurrentDictionary<MutableString, MutableString>/*!*/ _frozenStringLiterals =
            new ConcurrentDictionary<MutableString, MutableString>(new FrozenStringLiteralComparer());

        /// <summary>
        /// Exact identity of bytes and encoding, which is #eql? rather than #== - the latter calls
        /// a zero length string comparable with any other whatever its encoding, and two literals
        /// that disagree about their encoding must not share a frozen instance.
        /// </summary>
        private sealed class FrozenStringLiteralComparer : IEqualityComparer<MutableString> {
            public bool Equals(MutableString x, MutableString y) {
                return x.Encoding == y.Encoding && x.Equals(y);
            }

            public int GetHashCode(MutableString str) {
                return str.GetHashCode() ^ str.Encoding.GetHashCode();
            }
        }

        private static MutableString/*!*/ InternFrozenStringLiteral(MutableString/*!*/ str) {
            return _frozenStringLiterals.GetOrAdd(str.Freeze(), str);
        }

        /// <summary>
        /// The same table, for the other thing MRI puts in it: a String loaded by
        /// `Marshal.load(..., freeze: true)'.
        /// </summary>
        public static MutableString/*!*/ InternFrozenString(MutableString/*!*/ str) {
            return InternFrozenStringLiteral(str);
        }

        // The StrongBox is one per literal in the program, so the table is consulted once per
        // literal however often it is evaluated - the same shape the regexp literals use.
        [Emitted]
        public static MutableString/*!*/ CreateFrozenMutableStringL(string/*!*/ str1, RubyEncoding/*!*/ encoding, StrongBox<MutableString>/*!*/ cache) {
            return cache.Value ?? (cache.Value = InternFrozenStringLiteral(MutableString.Create(str1, encoding)));
        }

        [Emitted]
        public static MutableString/*!*/ CreateFrozenMutableStringB(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding, StrongBox<MutableString>/*!*/ cache) {
            return cache.Value ?? (cache.Value = InternFrozenStringLiteral(MutableString.CreateBinary(bytes, encoding)));
        }

        // Only emitted under --debug-frozen-string-literal, which is why the site can be threaded
        // through as a constant without costing an ordinary run anything.
        [Emitted]
        public static MutableString/*!*/ CreateFrozenMutableStringL(string/*!*/ str1, RubyEncoding/*!*/ encoding, StrongBox<MutableString>/*!*/ cache, string/*!*/ site) {
            return cache.Value ?? (cache.Value = MutableString.RecordLiteralSite(
                InternFrozenStringLiteral(MutableString.Create(str1, encoding)), site));
        }

        [Emitted]
        public static MutableString/*!*/ CreateFrozenMutableStringB(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding, StrongBox<MutableString>/*!*/ cache, string/*!*/ site) {
            return cache.Value ?? (cache.Value = MutableString.RecordLiteralSite(
                InternFrozenStringLiteral(MutableString.CreateBinary(bytes, encoding)), site));
        }

        /// <summary>
        /// A literal in a file that said nothing about frozen_string_literal. Mutable, but it
        /// warns the first time it is mutated - see MutableString.Chill.
        /// </summary>
        [Emitted]
        public static MutableString/*!*/ CreateChilledMutableStringL(string/*!*/ str1, RubyEncoding/*!*/ encoding) {
            return MutableString.Create(str1, encoding).Chill();
        }

        [Emitted]
        public static MutableString/*!*/ CreateChilledMutableStringB(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            return MutableString.CreateBinary(bytes, encoding).Chill();
        }

        [Emitted]
        public static MutableString/*!*/ CreateChilledMutableStringL(string/*!*/ str1, RubyEncoding/*!*/ encoding, string/*!*/ site) {
            return MutableString.RecordLiteralSite(MutableString.Create(str1, encoding).Chill(), site);
        }

        [Emitted]
        public static MutableString/*!*/ CreateChilledMutableStringB(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding, string/*!*/ site) {
            return MutableString.RecordLiteralSite(MutableString.CreateBinary(bytes, encoding).Chill(), site);
        }

        #endregion

        [Emitted]
        public static MutableString/*!*/ CreateMutableStringLM(string/*!*/ str1, MutableString str2, RubyEncoding/*!*/ encoding) {
            return MutableString.CreateMutable(str1, encoding).Append(str2);
        }

        [Emitted]
        public static MutableString/*!*/ CreateMutableStringML(MutableString str1, string/*!*/ str2, RubyEncoding/*!*/ encoding) {
            return MutableString.CreateInternal(str1, encoding).Append(str2);
        }

        [Emitted]
        public static MutableString/*!*/ CreateMutableStringMM(MutableString str1, MutableString str2, RubyEncoding/*!*/ encoding) {
            var result = MutableString.TryConcatenateCharacters(str1, str2, encoding);
            if (result != null) {
                return result;
            }

            result = MutableString.CreateInternal(str1, encoding);
            AppendInterpolatedPart(result, str2);
            return result;
        }

        /// <summary>
        /// MRI's rb_str_concat_literals: append, and a result that is still US-ASCII takes the encoding
        /// of the first part that is not - "#{1}a" in a UTF-8 file is UTF-8, "#{y}#{bin}" is binary.
        /// </summary>
        private static void AppendInterpolatedPart(MutableString/*!*/ result, MutableString part) {
            if (part != null) {
                result.Append(part);
                if (part.Encoding != RubyEncoding.Ascii && result.Encoding == RubyEncoding.Ascii) {
                    result.ForceEncoding(part.Encoding);
                }
            }
        }

        // TODO: we should emit Append calls directly, and not create an array first
        [Emitted]
        public static MutableString/*!*/ CreateMutableStringN(MutableString/*!*/[]/*!*/ parts) {
            Debug.Assert(parts.Length > 0);
            var concatenated = MutableString.TryConcatenateCharacters(parts);
            if (concatenated != null) {
                return concatenated;
            }

            // room for all of it up front rather than a regrowth per part (a part counts its
            // characters or its bytes, whichever it holds, which is what a capacity hint needs)
            int capacity = 0;
            for (int i = 0; i < parts.Length; i++) {
                if (parts[i] != null) {
                    capacity += parts[i].Length;
                }
            }
            // MRI's rb_str_concat_literals starts from the first part's encoding: "a#{1}b" in a UTF-8
            // file is UTF-8, though every part is ASCII and the Integer's is US-ASCII
            var result = MutableString.CreateMutable(capacity, (parts[0] != null) ? parts[0].Encoding : RubyEncoding.Ascii);

            for (int i = 0; i < parts.Length; i++) {
                AppendInterpolatedPart(result, parts[i]);
            }

            return result;
        }

        #endregion

        #region CreateSymbol

        [Emitted]
        public static RubySymbol/*!*/ CreateSymbolM(MutableString str1, RubyEncoding/*!*/ encoding, RubyScope/*!*/ scope) {
            return CreateInterpolatedSymbol(scope.RubyContext, CreateMutableStringM(str1, encoding));
        }

        [Emitted]
        public static RubySymbol/*!*/ CreateSymbolLM(string/*!*/ str1, MutableString str2, RubyEncoding/*!*/ encoding, RubyScope/*!*/ scope) {
            return CreateInterpolatedSymbol(scope.RubyContext, CreateMutableStringLM(str1, str2, encoding));
        }

        [Emitted]
        public static RubySymbol/*!*/ CreateSymbolML(MutableString str1, string/*!*/ str2, RubyEncoding/*!*/ encoding, RubyScope/*!*/ scope) {
            return CreateInterpolatedSymbol(scope.RubyContext, CreateMutableStringML(str1, str2, encoding));
        }

        [Emitted]
        public static RubySymbol/*!*/ CreateSymbolMM(MutableString str1, MutableString str2, RubyEncoding/*!*/ encoding, RubyScope/*!*/ scope) {
            return CreateInterpolatedSymbol(scope.RubyContext, CreateMutableStringMM(str1, str2, encoding));
        }

        [Emitted]
        public static RubySymbol/*!*/ CreateSymbolN(MutableString[]/*!*/ strings, RubyScope/*!*/ scope) {
            return CreateInterpolatedSymbol(scope.RubyContext, CreateMutableStringN(strings));
        }

        /// <summary>
        /// :"#{...}" and %I[#{...}] are String#to_sym of what was interpolated: bytes that are not
        /// valid in the string's encoding cannot name a symbol (MRI's rb_str_intern), where an
        /// ASCII byte means itself.
        /// </summary>
        private static RubySymbol/*!*/ CreateInterpolatedSymbol(RubyContext/*!*/ context, MutableString/*!*/ str) {
            if (str.Encoding.IsAsciiIdentity && str.ContainsInvalidCharacters()) {
                throw new EncodingError(RubyExceptions.FormatMessage("invalid symbol in encoding {0} :{1}",
                    str.Encoding.Name, context.Inspect(str).ToString()));
            }
            return context.CreateSymbol(str, false);
        }

        #endregion

        #region Strings, Encodings

        [Emitted]
        public static RubyEncoding/*!*/ CreateEncoding(int codepage) {
            return RubyEncoding.GetRubyEncoding(codepage);
        }

        [Emitted, Obsolete("Internal only")]
        public static byte[]/*!*/ GetMutableStringBytes(MutableString/*!*/ str) {

            int byteCount;
            var result = str.GetByteArray(out byteCount);
            return result;
        }

        #endregion

        #region Booleans

        [Emitted]
        public static bool IsTrue(object obj) {
            return (obj is bool) ? (bool)obj == true : obj != null;
        }

        [Emitted]
        public static bool IsFalse(object obj) {
            return (obj is bool) ? (bool)obj == false : obj == null;
        }

        [Emitted]
        public static object NullIfFalse(object obj) {
            return (obj is bool && !(bool)obj) ? null : obj;
        }

        [Emitted]
        public static object NullIfTrue(object obj) {
            return (obj is bool && !(bool)obj || obj == null) ? DefaultArgument : null;
        }

        #endregion

        #region Exceptions

        //
        // NOTE:
        // Exception Ops go directly to the current exception object. MRI ignores potential aliases.
        //

        /// <summary>
        /// Called in try-filter that wraps the entire top-level code. 
        /// We just need to capture stack trace, should not filter out any exception.
        /// </summary>
        [Emitted]
        public static bool TraceTopLevelCodeFrame(RubyScope/*!*/ scope, Exception/*!*/ exception) {
            RubyExceptionData.GetInstance(exception).CaptureExceptionTrace(scope);
            return false;
        }

        /// <summary>
        /// MRI 3.4+ (vm_insnhelper.c warn_unused_block): a block passed from Ruby code to a method
        /// that never uses it may be ignored. Reported once per method, and - unless
        /// Warning[:strict_unused_block] is on - not at all for a name some method that does use a
        /// block also has, since the call may well have meant that one.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex _ironRubyLibraryFile =
            new System.Text.RegularExpressions.Regex(@"[/\\](StdLib|Lib)[/\\]ironruby[/\\]");

        [Emitted]
        public static void WarnUnusedBlock(Proc block, RubyMethodInfo/*!*/ method) {
            if (block == null || method.Body.UnusedBlockReported) {
                return;
            }

            var context = method.Context;
            bool strict = context.IsWarningEnabled("strict_unused_block");
            if (!strict && context.IsMethodNameUsingBlock(method.DefinitionName)) {
                return;
            }

            method.Body.UnusedBlockReported = true;
            string file = method.Document != null ? method.Document.FileName : null;
            if (file != null && _ironRubyLibraryFile.IsMatch(file)) {
                // IronRuby's own Ruby implementations of what MRI writes in C, which never warns
                return;
            }

            if (context.Verbose is bool && ((bool)context.Verbose || strict)) {
                string label = IronRuby.Compiler.Ast.MethodDefinition.QualifyFrameLabel(method.DefinitionName, method.DeclaringModule);
                context.ReportWarning(file != null
                    ? String.Format("the block passed to '{0}' defined at {1}:{2} may be ignored", label, file, method.SourceSpan.Start.Line)
                    : String.Format("the block may be ignored because '{0}' does not use a block", label)
                );
            }
        }

        /// <summary>
        /// The exception filter around a file's top-level code: a return from a block at the top
        /// level unwinds to here and ends the file; anything else gets the frame traced and passes.
        /// </summary>
        [Emitted]
        public static bool IsTopLevelReturn(RubyScope/*!*/ scope, Exception/*!*/ exception) {
            var unwinder = exception as MethodUnwinder;
            if (unwinder != null && unwinder.TargetFrame == scope) {
                return true;
            }
            RubyExceptionData.GetInstance(exception).CaptureExceptionTrace(scope);
            return false;
        }

        // Ruby method exit filter. It only sees MethodUnwinders: the backtrace of any other exception
        // is captured by whatever handles it (see MethodDefinition.TransformBody).
        [Emitted]
        public static bool IsMethodUnwinderTargetFrame(RubyScope/*!*/ scope, Exception/*!*/ exception) {
            var unwinder = exception as MethodUnwinder;
            return unwinder != null && unwinder.TargetFrame == scope.FlowControlScope;
        }

        [Emitted]
        public static object GetMethodUnwinderReturnValue(Exception/*!*/ exception) {
            return ((MethodUnwinder)exception).ReturnValue;
        }

        [Emitted]
        public static void LeaveMethodFrame(RuntimeFlowControl/*!*/ rfc) {
            // no write to a frame that is not active: CreateRfcForMethod's shared inactive frame stays untouched
            if (rfc._activeFlowControlScope != null) {
                rfc.LeaveMethod();
                ReleaseScopeFrame(rfc as RubyScope);
            }
        }

        // A captured scope needs its locals, not a completed interpreter frame's temporary
        // operands (which may include the Fiber handle returned by its factory method).
        [Emitted]
        public static void ReleaseScopeFrame(RubyScope scope) {
            if (scope != null) {
                scope.InterpretedFrame = null;
            }
        }

        /// <summary>
        /// Filters exceptions raised from EH-body, EH-rescue and EH-else clauses.
        /// </summary>
        [Emitted]
        public static bool CanRescue(RubyScope/*!*/ scope, Exception/*!*/ exception) {
            // A CLR finally may replace the reclaim signal with an ordinary exception.
            // That still belongs to a discarded stack and must not enter a Ruby rescue.
            if (RubyUtils.IsReclaimingFiber) {
                return false;
            }
            if (exception is StackUnwinder) {
                return false;
            }

            LocalJumpError lje = exception as LocalJumpError;
            if (lje != null && lje.SkipFrame == scope.FlowControlScope) {
                return false;
            }

            // calls "new" on the exception class if it hasn't been called yet:
            exception = RubyExceptionData.HandleException(scope, exception);

            // Exception#cause: the exception that was being handled when this one was raised.
            // $! still holds it here - we are about to overwrite it below. Kernel#raise has
            // already decided the cause for the exceptions it throws, and TrySetCause leaves
            // those (and any re-raise of an already-raised exception) alone.
            // One lookup, not two: the data lives in Exception.Data, whose store is a linear scan.
            var data = RubyExceptionData.GetInstance(exception);
            data.TrySetCause(scope.RubyContext.CurrentException);

            scope.RubyContext.CurrentException = exception;
            data.CaptureExceptionTrace(scope);
            return true;
        }

        [Emitted]
        public static Exception/*!*/ MarkException(Exception/*!*/ exception) {
            RubyExceptionData.GetInstance(exception).Handled = true;
            return exception;
        }

        [Emitted]
        public static Exception GetCurrentException(RubyScope/*!*/ scope) {
            return scope.RubyContext.CurrentException;
        }

        /// <summary>
        /// Sets $!. Used in EH finally clauses to restore exception stored in oldExceptionVariable local.
        /// </summary>
        [Emitted] 
        public static void SetCurrentException(RubyScope/*!*/ scope, Exception exception) {
            scope.RubyContext.CurrentException = exception;
        }

        [Emitted] //RescueClause:
        public static bool CompareException(BinaryOpStorage/*!*/ comparisonStorage, RubyScope/*!*/ scope, object classObject) {            
            var context = scope.RubyContext;
            if (!(classObject is RubyModule)) {
                throw RubyExceptions.CreateTypeError("class or module required for rescue clause");
            }

            var site = comparisonStorage.GetCallSite("===");
            bool result = IsTrue(site.Target(site, classObject, context.CurrentException));
            if (result) {
                TraceRescue(scope, context.CurrentException);
            }
            return result;
        }

        [Emitted] //RescueClause:
        public static bool CompareSplattedExceptions(BinaryOpStorage/*!*/ comparisonStorage, RubyScope/*!*/ scope, IList/*!*/ classObjects) {
            for (int i = 0; i < classObjects.Count; i++) {
                if (CompareException(comparisonStorage, scope, classObjects[i])) {
                    return true;
                }
            }
            return false;
        }

        [Emitted] //RescueClause:
        public static bool CompareDefaultException(BinaryOpStorage/*!*/ comparisonStorage, RubyScope/*!*/ scope) {
            // a bare rescue is "rescue StandardError": CRuby calls StandardError.=== too
            return CompareException(comparisonStorage, scope, scope.RubyContext.StandardErrorClass);
        }

        private static void TraceRescue(RubyScope/*!*/ scope, Exception exception) {
            if ((TracePoint.ActiveEvents & (int)TraceEvents.Rescue) != 0 && exception != null) {
                TracePoint.OnException(TraceEvents.Rescue, scope, scope.RubyContext, exception);
            }
        }

        [Emitted]
        public static string/*!*/ GetDefaultExceptionMessage(RubyClass/*!*/ exceptionClass) {
            return exceptionClass.Name;
        }

        [Emitted]
        public static ArgumentException/*!*/ CreateArgumentsError(string message) {
            return (ArgumentException)RubyExceptions.CreateArgumentError(message);
        }

        [Emitted]
        public static ArgumentException/*!*/ CreateArgumentsErrorForMissingBlock() {
            return (ArgumentException)RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
        }

        [Emitted]
        public static ArgumentException/*!*/ CreateArgumentsErrorForProc(string className) {
            return (ArgumentException)RubyExceptions.CreateArgumentError(String.Format("wrong type argument {0} (should be callable)", className));
        }

        [Emitted]
        public static ArgumentException/*!*/ MakeWrongNumberOfArgumentsError(int actual, int expected) {
            // MRI wording since 1.9: "wrong number of arguments (given 1, expected 0)".
            return new ArgumentException(String.Format("wrong number of arguments (given {0}, expected {1})", actual, expected));
        }

        /// <summary>
        /// Same, but with MRI's variable-arity spelling of the expected count: "1..3", "1+" or "2, 3, or 5".
        /// The description is a compile-time constant produced by the binder.
        /// </summary>
        [Emitted]
        public static ArgumentException/*!*/ MakeWrongNumberOfArgumentsErrorN(int actual, string/*!*/ expected) {
            return new ArgumentException(String.Format("wrong number of arguments (given {0}, expected {1})", actual, expected));
        }

        [Emitted] //SuperCall
        public static Exception/*!*/ MakeTopLevelSuperException() {
            return new MissingMethodException("super called outside of method");
        }

        [Emitted] //SuperCallAction
        public static Exception/*!*/ MakeMissingSuperException(RubyContext/*!*/ context, object self, string/*!*/ name) {
            return RubyExceptions.CreateNoSuperclassMethod(context, self, name);
        }

        [Emitted]
        public static Exception/*!*/ MakeVirtualClassInstantiatedError() {
            return RubyExceptions.CreateTypeError("can't create instance of virtual class");
        }

        [Emitted]
        public static Exception/*!*/ MakeUninitializedClassInstantiatedError() {
            return RubyExceptions.CreateTypeError("can't instantiate uninitialized class");
        }

        [Emitted]
        public static Exception/*!*/ MakeTypeConversionError(RubyContext/*!*/ context, object value, Type/*!*/ type) {
            return RubyExceptions.CreateTypeConversionError(context.GetClassDisplayName(value), context.GetTypeName(type, true));
        }

        [Emitted]
        public static Exception/*!*/ MakeAmbiguousMatchError(string/*!*/ message) {
            // TODO:
            return new AmbiguousMatchException(message);
        }

        [Emitted]
        public static Exception/*!*/ MakeAllocatorUndefinedError(RubyClass/*!*/ classObj) {
            return RubyExceptions.CreateAllocatorUndefinedError(classObj);
        }

        [Emitted]
        public static Exception/*!*/ MakeNotClrTypeError(RubyClass/*!*/ classObj) {
            return RubyExceptions.CreateNotClrTypeError(classObj);
        }

        [Emitted]
        public static Exception/*!*/ MakeConstructorUndefinedError(RubyClass/*!*/ classObj) {
            return RubyExceptions.CreateTypeError(String.Format("'{0}' doesn't have a visible CLR constructor", 
                classObj.Context.GetTypeName(classObj.TypeTracker.Type, true)
            ));
        }

        [Emitted]
        public static Exception/*!*/ MakeMissingDefaultConstructorError(RubyClass/*!*/ classObj, string/*!*/ initializerOwnerName) {
            return RubyExceptions.CreateMissingDefaultConstructorError(classObj, initializerOwnerName);
        }

        [Emitted]
        public static Exception/*!*/ MakePrivateMethodCalledError(RubyContext/*!*/ context, object target, string/*!*/ methodName) {
            return RubyExceptions.CreatePrivateMethodCalled(context, target, methodName);
        }

        [Emitted]
        public static Exception/*!*/ MakeProtectedMethodCalledError(RubyContext/*!*/ context, object target, string/*!*/ methodName) {
            return RubyExceptions.CreateProtectedMethodCalled(context, target, methodName);
        }

        [Emitted]
        public static Exception/*!*/ MakeClrProtectedMethodCalledError(RubyContext/*!*/ context, object target, string/*!*/ methodName) {
            return new MissingMethodException(
                RubyExceptions.FormatMethodMissingMessage(context, target, methodName, "CLR protected method '{0}' called for {1}; " +
                "CLR protected methods can only be called with a receiver whose class is a Ruby subclass of the class declaring the method")
            );
        }

        [Emitted]
        public static Exception/*!*/ MakeClrVirtualMethodCalledError(RubyContext/*!*/ context, object target, string/*!*/ methodName) {
            return new MissingMethodException(
                RubyExceptions.FormatMethodMissingMessage(context, target, methodName, "Virtual CLR method '{0}' called via super from {1}; " +
                "Super calls to virtual CLR methods can only be used in a Ruby subclass of the class declaring the method")
            );
        }

        [Emitted]
        public static Exception/*!*/ MakeImplicitSuperInBlockMethodError() {
            return RubyExceptions.CreateRuntimeError("implicit argument passing of super from method defined by define_method() is not supported. Specify all arguments explicitly.");
        }

        [Emitted]
        public static Exception/*!*/ MakeMissingMethodError(RubyContext/*!*/ context, object self, string/*!*/ methodName) {
            return RubyExceptions.CreateMethodMissing(context, self, methodName);
        }

        // MRI's method_missing_reason: whether the call that is about to reach a user-defined
        // method_missing had an implicit receiver, and whether it was a bare name that could have
        // been a local variable, read back by BasicObject#method_missing when that definition
        // calls super, so that NoMethodError#private_call? can answer and a bare name raises
        // NameError ("undefined local variable or method").
        [ThreadStatic]
        private static bool _methodMissingPrivateCall;
        [ThreadStatic]
        private static bool _methodMissingVariableCall;

        public static bool MethodMissingPrivateCall {
            get { return _methodMissingPrivateCall; }
            set { _methodMissingPrivateCall = value; }
        }

        public static bool MethodMissingVariableCall {
            get { return _methodMissingVariableCall; }
            set { _methodMissingVariableCall = value; }
        }

        [Emitted]
        public static void SetMethodMissingReason(bool privateCall, bool variableCall) {
            _methodMissingPrivateCall = privateCall;
            _methodMissingVariableCall = variableCall;
        }

        [Emitted]
        public static Exception/*!*/ SetMissingMethodArguments(Exception/*!*/ error, object[]/*!*/ args, IList splat, object rhs, bool hasRhs, bool privateCall) {
            var list = new RubyArray(args);
            if (splat != null) {
                list.AddRange(splat);
            }
            if (hasRhs) {
                list.Add(rhs);
            }
            var data = RubyExceptionData.GetInstance(error);
            data.Arguments = list;
            data.PrivateCall = privateCall;
            return error;
        }

        /// <summary>
        /// The frame of the method an ArgumentError for the wrong number of arguments was raised for:
        /// the backtrace starts with it, as MRI's does (see MetaObjectBuilder.AddCalleeFrame). No file
        /// for a library method, whose frame is at the call.
        /// </summary>
        [Emitted]
        public static Exception/*!*/ SetCalleeFrame(Exception/*!*/ error, string file, int line, string/*!*/ label) {
            RubyExceptionData.GetInstance(error).SetCalleeFrame(file, line, label);
            return error;
        }

        [Emitted]
        public static Exception/*!*/ MakeUndefinedLocalOrMethodError(RubyContext/*!*/ context, RubyScope/*!*/ scope, object self, string/*!*/ methodName) {
            var error = RubyExceptions.CreateUndefinedLocalOrMethod(context, self, methodName);
            // NameError#local_variables: the locals the name might have been meant as (did_you_mean)
            RubyExceptionData.GetInstance(error).LocalVariables = scope.GetLocalVariableSymbols();
            return error;
        }

        [Emitted]
        public static Exception/*!*/ MakeMissingMemberError(string/*!*/ memberName) {
            return new MissingMemberException(String.Format(CultureInfo.InvariantCulture, "undefined member: '{0}'", memberName));
        }

        #endregion

        #region Ranges

        [Emitted]
        public static Range/*!*/ CreateInclusiveRange(object begin, object end, RubyScope/*!*/ scope, BinaryOpStorage/*!*/ comparisonStorage) {
            return new Range(comparisonStorage, scope.RubyContext, begin, end, false);
        }

        [Emitted]
        public static Range/*!*/ CreateExclusiveRange(object begin, object end, RubyScope/*!*/ scope, BinaryOpStorage/*!*/ comparisonStorage) {
            return new Range(comparisonStorage, scope.RubyContext, begin, end, true);
        }

        #endregion

        #region Dynamic Operations

        // allocator for struct instances:
        [Emitted]
        public static RubyStruct/*!*/ AllocateStructInstance(RubyClass/*!*/ self) {
            return RubyStruct.Create(self);
        }

        // factory for struct instances:
        [Emitted]
        public static RubyStruct/*!*/ CreateStructInstance(RubyClass/*!*/ self, [NotNull]params object[]/*!*/ items) {
            var result = RubyStruct.Create(self);
            result.SetValues(items);
            return result;
        }

        [Emitted]
        public static DynamicMetaObject/*!*/ GetMetaObject(IRubyObject/*!*/ obj, MSA.Expression/*!*/ parameter) {
            return new RubyObject.Meta(parameter, BindingRestrictions.Empty, obj);
        }

        [Emitted]
        public static RubyMethod/*!*/ CreateBoundMember(object target, RubyMemberInfo/*!*/ info, string/*!*/ name) {
            return new RubyMethod(target, info, name);
        }

        [Emitted]
        public static RubyMethod/*!*/ CreateBoundMissingMember(object target, RubyMemberInfo/*!*/ info, string/*!*/ name) {
            return RubyMethod.CreateMethodMissing(target, info, name);
        }

        [Emitted]
        public static bool IsClrSingletonRuleValid(RubyContext/*!*/ context, object/*!*/ target, int expectedVersion) {
            RubyInstanceData data;
            RubyClass immediate;

            // TODO: optimize this (we can have a hashtable of singletons per class: Weak(object) => Struct { ImmediateClass, InstanceVariables, Flags }):
            return context.TryGetClrTypeInstanceData(target, out data) && (immediate = data.ImmediateClass) != null && immediate.IsSingletonClass
                && immediate.Version.Method == expectedVersion;
        }

        [Emitted]
        public static bool IsClrNonSingletonRuleValid(RubyContext/*!*/ context, object/*!*/ target, VersionHandle/*!*/ versionHandle, int expectedVersion) {
            RubyInstanceData data;
            RubyClass immediate;

            return versionHandle.Method == expectedVersion
                // TODO: optimize this (we can have a hashtable of singletons per class: Weak(object) => Struct { ImmediateClass, InstanceVariables, Flags }):
                && !(context.TryGetClrTypeInstanceData(target, out data) && (immediate = data.ImmediateClass) != null
                    && (immediate.IsSingletonClass || immediate.IsRubyClass));
        }

        // :c_call / :c_return around a library method call; only in rules bound while TracePoint.CCallTracing
        [Emitted]
        public static void TraceLibraryCall(RubyScope scope, object self, RubyMemberInfo/*!*/ method, string/*!*/ name) {
            TracePoint.OnLibraryCall(TraceEvents.CCall, scope, self, method, name, null);
        }

        [Emitted]
        public static void TraceLibraryReturn(RubyScope scope, object self, RubyMemberInfo/*!*/ method, string/*!*/ name, object value) {
            TracePoint.OnLibraryCall(TraceEvents.CReturn, scope, self, method, name, value);
        }

        /// <summary>
        /// A rule for an object of a sealed CLR type whose class is a Ruby subclass of that type (see
        /// RubyContext.AdoptClrObject) holds for objects with exactly that immediate class.
        /// </summary>
        [Emitted]
        public static bool IsAdoptedClrRuleValid(RubyContext/*!*/ context, object/*!*/ target, RubyClass/*!*/ expectedClass, int expectedVersion) {
            RubyInstanceData data;
            return context.TryGetClrTypeInstanceData(target, out data) && ReferenceEquals(data.ImmediateClass, expectedClass)
                && expectedClass.Version.Method == expectedVersion;
        }

        // super call condition
        [Emitted]
        public static object GetSuperCallTarget(RubyScope/*!*/ scope, int targetId) {
            while (true) {
                switch (scope.Kind) {
                    case ScopeKind.Method:
                        return targetId == 0 ? scope.SelfObject : NeedsUpdate;

                    case ScopeKind.BlockMethod:
                        return targetId == ((RubyBlockScope)scope).BlockFlowControl.Proc.Method.Id ? scope.SelfObject : NeedsUpdate;

                    case ScopeKind.TopLevel:
                        // This method is only called if there was method or block-method scope in lexical scope chain.
                        // Once there is it cannot be undone. It can only be shadowed by a block scope that became block-method scope, or
                        // a block-method scope's target-id can be changed.
                        throw Assert.Unreachable;
                }

                scope = scope.Parent;
            }
        }

        // super call condition
        [Emitted]
        public static bool IsSuperOutOfMethodScope(RubyScope/*!*/ scope) {
            while (true) {
                switch (scope.Kind) {
                    case ScopeKind.Method:
                    case ScopeKind.BlockMethod:
                        return false;

                    case ScopeKind.TopLevel:
                        return true;
                }

                scope = scope.Parent;
            }
        }

        #endregion

        #region Conversions

        [Emitted] // ProtocolConversionAction
        public static Proc/*!*/ ToProcValidator(string/*!*/ className, object obj) {
            Proc result = obj as Proc;
            if (result == null) {
                throw RubyExceptions.CreateReturnTypeError(className, "to_proc", "Proc", obj);
            }
            return result;
        }

        // Used for implicit conversions from System.String to MutableString (to_str conversion like).
        [Emitted]
        public static MutableString/*!*/ StringToMutableString(string/*!*/ str) {
            return MutableString.Create(str, RubyEncoding.UTF8);
        }

        // Used for implicit conversions from System.Object to MutableString (to_s conversion like).
        [Emitted]
        public static MutableString/*!*/ ObjectToMutableString(object/*!*/ value) {
            return (value != null) ? MutableString.Create(value.ToString(), RubyEncoding.UTF8) : MutableString.FrozenEmpty;
        }

        /// <summary>MRI's rb_check_string_type: a #to_str answering nil is "not a String after all".</summary>
        [Emitted] // ProtocolConversionAction
        public static MutableString TryToStringValidator(string/*!*/ className, object obj) {
            return (obj == null) ? null : ToStringValidator(className, obj);
        }

        [Emitted] // ProtocolConversionAction
        public static MutableString/*!*/ ToStringValidator(string/*!*/ className, object obj) {
            MutableString result = obj as MutableString;
            if (result == null) {
                throw RubyExceptions.CreateReturnTypeError(className, "to_str", "String", obj);
            }
            return result;
        }

        [Emitted] // ProtocolConversionAction
        public static string/*!*/ ToSymbolValidator(string/*!*/ className, object obj) {
            var str = obj as MutableString;
            if (str == null) {
                throw RubyExceptions.CreateReturnTypeError(className, "to_str", "String", obj); 
            }
            return str.ConvertToString();
        }

        [Emitted] // ProtocolConversionAction
        public static string/*!*/ ConvertSymbolToClrString(RubySymbol/*!*/ value) {
            return value.ToString();
        }

        [Emitted] // ProtocolConversionAction
        public static string/*!*/ ConvertMutableStringToClrString(MutableString/*!*/ value) {
            return value.ConvertToString();
        }

        [Emitted] // ProtocolConversionAction
        public static RubyRegex/*!*/ ToRegexValidator(string/*!*/ className, object obj) {
            return RubyRegex.FromLiteralPattern(ToStringValidator(className, obj));
        }

        /// <summary>
        /// MRI's rb_check_array_type: a #to_ary that answers nil means "not an Array after all",
        /// which is not an error - the caller keeps the object as it is. Anything else that is not
        /// an Array is a broken promise and still raises.
        /// </summary>
        [Emitted] // ProtocolConversionAction
        public static IList TryToArrayValidator(string/*!*/ className, object obj) {
            return (obj == null) ? null : ToArrayValidator(className, obj);
        }

        [Emitted] // ProtocolConversionAction
        public static IList TryToAValidator(string/*!*/ className, object obj) {
            return (obj == null) ? null : ToAValidator(className, obj);
        }

        [Emitted] // ProtocolConversionAction
        public static IDictionary<object, object> TryToHashValidator(string/*!*/ className, object obj) {
            return (obj == null) ? null : ToHashValidator(className, obj);
        }

        [Emitted] // ImplicitTrySplatAction
        public static object TrySplatToAryValidator(string/*!*/ className, object splattee, object obj) {
            return (obj == null) ? splattee : ToArrayValidator(className, obj);
        }

        [Emitted] // ProtocolConversionAction
        public static IList/*!*/ ToArrayValidator(string/*!*/ className, object obj) {
            var result = obj as IList;
            if (result == null) {
                throw RubyExceptions.CreateReturnTypeError(className, "to_ary", "Array", obj);
            }
            return result;
        }

        [Emitted] // ProtocolConversionAction
        public static IList/*!*/ ToAValidator(string/*!*/ className, object obj) {
            var result = obj as IList;
            if (result == null) {
                throw RubyExceptions.CreateReturnTypeError(className, "to_a", "Array", obj);
            }
            return result;
        }

        /// <summary>
        /// Validates the result of #to_a called on a splatted argument. MRI's rb_check_array_type:
        /// a #to_a that answers nil means "not an Array after all" and the splattee itself becomes
        /// the single element; any other non-Array result is a broken promise and raises.
        /// </summary>
        [Emitted] // ProtocolConversionAction
        public static IList/*!*/ SplatToAValidator(string/*!*/ className, object splattee, object obj) {
            if (obj == null) {
                var wrapped = new RubyArray(1);
                wrapped.Add(splattee);
                return wrapped;
            }
            var result = obj as IList;
            if (result == null) {
                throw RubyExceptions.CreateReturnTypeError(className, "to_a", "Array", obj);
            }
            return result;
        }

        /// <summary>Same for #to_ary (multiple assignment).</summary>
        [Emitted] // ProtocolConversionAction
        public static IList/*!*/ SplatToAryValidator(string/*!*/ className, object splattee, object obj) {
            if (obj == null) {
                var wrapped = new RubyArray(1);
                wrapped.Add(splattee);
                return wrapped;
            }
            var result = obj as IList;
            if (result == null) {
                throw RubyExceptions.CreateReturnTypeError(className, "to_ary", "Array", obj);
            }
            return result;
        }

        [Emitted] // ProtocolConversionAction
        public static IDictionary<object, object>/*!*/ ToHashValidator(string/*!*/ className, object obj) {
            var result = obj as IDictionary<object, object>;
            if (result == null) {
                throw RubyExceptions.CreateReturnTypeError(className, "to_hash", "Hash", obj);
            }
            return result;
        }

        private static int ToIntValidator(string/*!*/ className, string/*!*/ targetType, object obj) {
            if (obj is int) {
                return (int)obj;
            }

            if (obj is long wide) {
                if (wide >= Int32.MinValue && wide <= Int32.MaxValue) {
                    return (int)wide;
                }
                throw RubyExceptions.CreateRangeError("bignum too big to convert into {0}", targetType);
            }

            if (obj is BigInteger bignum) {
                int fixnum;
                if (bignum.AsInt32(out fixnum)) {
                    return fixnum;
                }
                throw RubyExceptions.CreateRangeError("bignum too big to convert into {0}", targetType);
            }

            throw RubyExceptions.CreateReturnTypeError(className, "to_int", "Integer", obj);
        }

        [Emitted] // ProtocolConversionAction
        public static int ToFixnumValidator(string/*!*/ className, object obj) {
            return ToIntValidator(className, "Integer", obj);
        }

        [Emitted] // ProtocolConversionAction
        public static Byte ToByteValidator(string/*!*/ className, object obj) {
            return Converter.ToByte(ToIntValidator(className, "System::Byte", obj));
        }

        [Emitted] // ProtocolConversionAction
        public static SByte ToSByteValidator(string/*!*/ className, object obj) {
            return Converter.ToSByte(ToIntValidator(className, "System::SByte", obj));
        }

        [Emitted] // ProtocolConversionAction
        public static Int16 ToInt16Validator(string/*!*/ className, object obj) {
            return Converter.ToInt16(ToIntValidator(className, "System::Int16", obj));
        }

        [Emitted] // ProtocolConversionAction
        public static UInt16 ToUInt16Validator(string/*!*/ className, object obj) {
            return Converter.ToUInt16(ToIntValidator(className, "System::UInt16", obj));
        }

        [Emitted] // ProtocolConversionAction
        public static UInt32 ToUInt32Validator(string/*!*/ className, object obj) {
            if (obj is int) {
                return Converter.ToUInt32((int)obj);
            }

            if (obj is long wide) {
                return Converter.ToUInt32((BigInteger)wide);
            }

            if (obj is BigInteger bignum) {
                return Converter.ToUInt32(bignum);
            }

            throw RubyExceptions.CreateReturnTypeError(className, "to_int", "Integer", obj);
        }

        [Emitted] // ProtocolConversionAction
        public static Int64 ToInt64Validator(string/*!*/ className, object obj) {
            if (obj is int) {
                return (int)obj;
            }

            if (obj is long wide) {
                return wide;
            }

            if (obj is BigInteger bignum) {
                return Converter.ToInt64(bignum);
            }

            throw RubyExceptions.CreateReturnTypeError(className, "to_int", "Integer", obj);
        }

        [Emitted] // ProtocolConversionAction
        public static UInt64 ToUInt64Validator(string/*!*/ className, object obj) {
            if (obj is int) {
                return Converter.ToUInt64((int)obj);
            }

            if (obj is long wide) {
                return Converter.ToUInt64((BigInteger)wide);
            }

            if (obj is BigInteger bignum) {
                return Converter.ToUInt64(bignum);
            }

            throw RubyExceptions.CreateReturnTypeError(className, "to_int", "Integer", obj);
        }

        [Emitted] // ProtocolConversionAction
        public static BigInteger ToBignumValidator(string/*!*/ className, object obj) {
            if (obj is int) {
                return (int)obj;
            }

            if (obj is long wide) {
                return wide;
            }

            if (obj is BigInteger bignum) {
                return bignum;
            }

            throw RubyExceptions.CreateReturnTypeError(className, "to_int", "Integer", obj);
        }

        [Emitted] // ProtocolConversionAction
        public static IntegerValue ToIntegerValidator(string/*!*/ className, object obj) {
            return ToIntegerValue(className, "to_int", obj);
        }

        [Emitted] // ProtocolConversionAction (Kernel#Integer's #to_i step)
        public static IntegerValue ToIValidator(string/*!*/ className, object obj) {
            return ToIntegerValue(className, "to_i", obj);
        }

        private static IntegerValue ToIntegerValue(string/*!*/ className, string/*!*/ methodName, object obj) {
            return obj switch {
                int fixnum => new IntegerValue(fixnum),
                long wide => new IntegerValue((BigInteger)wide),
                BigInteger bignum => new IntegerValue(bignum),
                _ => throw RubyExceptions.CreateReturnTypeError(className, methodName, "Integer", obj)
            };
        }

        [Emitted] // ProtocolConversionAction
        public static double ToDoubleValidator(string/*!*/ className, object obj) {
            if (obj is double) {
                return (double)obj;
            }

            if (obj is float) {
                return (double)(float)obj;
            }

            throw RubyExceptions.CreateReturnTypeError(className, "to_f", "Float", obj);
        }

        [Emitted] // ProtocolConversionAction
        public static float ToSingleValidator(string/*!*/ className, object obj) {
            if (obj is double) {
                return (float)(double)obj;
            }

            if (obj is float) {
                return (float)obj;
            }

            throw RubyExceptions.CreateReturnTypeError(className, "to_f", "System::Single", obj);
        }

        [Emitted]
        public static double ConvertBignumToFloat(BigInteger/*!*/ value) {
            double result;
            return value.TryToFloat64(out result) ? result : (value.IsNegative() ? Double.NegativeInfinity : Double.PositiveInfinity);
        }

        [Emitted]
        public static double ConvertMutableStringToFloat(RubyContext/*!*/ context, MutableString/*!*/ value) {
            return ConvertStringToFloat(context, value.ConvertToString(), value);
        }

        [Emitted]
        public static double ConvertStringToFloat(RubyContext/*!*/ context, string/*!*/ value) {
            return ConvertStringToFloat(context, value, null);
        }

        // MRI's Kernel#Float is written in Ruby, so its warning about a number out of range names the line of <internal:kernel>
        // that converts it rather than the caller's: the one for Float(x), or the one for Float(x, exception: false).
        private const int KernelFloatLine = 196, KernelFloatNoExceptionLine = 198;

        /// <summary>Kernel#Float's conversion of a String.</summary>
        public static double ConvertKernelFloat(RubyContext/*!*/ context, MutableString/*!*/ value) {
            return ConvertStringToFloat(context, value.ConvertToString(), value, KernelFloatLine);
        }

        /// <summary>Kernel#Float's conversion of a String with exception: false.</summary>
        public static bool TryConvertKernelFloat(RubyContext/*!*/ context, MutableString/*!*/ value, out double result) {
            string text = value.ConvertToString();
            if (!TryParseRubyFloat(text, out result)) {
                return false;
            }
            WarnIfFloatOutOfRange(context, text.Trim(_FloatWhitespace).Replace("_", ""), result, KernelFloatNoExceptionLine);
            return true;
        }

        private static double ConvertStringToFloat(RubyContext/*!*/ context, string/*!*/ value, MutableString original, int kernelFloatLine = 0) {
            double result;
            if (TryParseRubyFloat(value, out result)) {
                WarnIfFloatOutOfRange(context, value.Trim(_FloatWhitespace).Replace("_", ""), result, kernelFloatLine);
                return result;
            }

            throw InvalidFloatValue(original != null ? original.ToByteArray() : Encoding.UTF8.GetBytes(value));
        }

        private static readonly char[] _FloatWhitespace = { ' ', '\t', '\n', '\v', '\f', '\r' };

        /// <summary>
        /// MRI's rb_cstr_to_dbl warns (verbose only) when strtod over- or underflows, quoting at
        /// most 20 characters of the number it parsed, underscores taken out:
        ///   "Float 1e400 out of range"
        /// </summary>
        public static void WarnIfFloatOutOfRange(RubyContext/*!*/ context, string/*!*/ number, double result) {
            WarnIfFloatOutOfRange(context, number, result, 0);
        }

        private static void WarnIfFloatOutOfRange(RubyContext/*!*/ context, string/*!*/ number, double result, int kernelFloatLine) {
            if (!(context.Verbose is bool verbose && verbose)) {
                return;
            }
            if (!Double.IsInfinity(result) && !(result == 0 && HasNonZeroMantissa(number))) {
                return;
            }
            if (number.Length > 20) {
                number = number.Substring(0, 20) + "...";
            }
            if (kernelFloatLine != 0) {
                context.ReportWarningAt("<internal:kernel>", kernelFloatLine, "Float " + number + " out of range");
            } else {
                context.ReportWarning("Float " + number + " out of range", true);
            }
        }

        private static bool HasNonZeroMantissa(string/*!*/ number) {
            int i = (number.Length > 0 && (number[0] == '+' || number[0] == '-')) ? 1 : 0;
            bool hex = i + 1 < number.Length && number[i] == '0' && (number[i + 1] == 'x' || number[i + 1] == 'X');
            for (i += hex ? 2 : 0; i < number.Length; i++) {
                char c = number[i];
                if (hex ? (c == 'p' || c == 'P') : (c == 'e' || c == 'E')) {
                    break;
                }
                if ((c >= '1' && c <= '9') || (hex && ((c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// MRI quotes the string byte by byte, as a binary string's inspect would: every byte
        /// outside printable ASCII shows as \xNN, so a non-ASCII character is spelled out in its bytes.
        /// </summary>
        private static Exception/*!*/ InvalidFloatValue(byte[]/*!*/ bytes) {
            var text = new StringBuilder(bytes.Length + 8);
            foreach (byte b in bytes) {
                switch (b) {
                    case (byte)'\n': text.Append("\\n"); break;
                    case (byte)'\t': text.Append("\\t"); break;
                    case (byte)'\r': text.Append("\\r"); break;
                    case (byte)'\f': text.Append("\\f"); break;
                    case (byte)'\v': text.Append("\\v"); break;
                    case (byte)'\a': text.Append("\\a"); break;
                    case (byte)'\b': text.Append("\\b"); break;
                    case 27: text.Append("\\e"); break;
                    case (byte)'"': text.Append("\\\""); break;
                    case (byte)'\\': text.Append("\\\\"); break;
                    default:
                        if (b < 0x20 || b >= 0x7f) {
                            text.Append("\\x").Append(b.ToString("X2", CultureInfo.InvariantCulture));
                        } else {
                            text.Append((char)b);
                        }
                        break;
                }
            }
            return RubyExceptions.CreateArgumentError("invalid value for Float(): \"{0}\"", text.ToString());
        }

        #region Kernel#Float string grammar

        // Kernel#Float does not accept what C's strtod does. Surrounding whitespace is allowed
        // but an embedded NUL is not; '_' may separate digits; a trailing '.' is rejected while a
        // leading one is not; hexadecimal literals are accepted; "inf", "Infinity" and "nan" are
        // not. The rules below were derived by differential testing against CRuby 3.3.8.

        private static bool IsFloatWhitespace(char c) {
            return c == ' ' || (c >= '\t' && c <= '\r');
        }

        private static bool IsDecimalDigit(char c) {
            return c >= '0' && c <= '9';
        }

        private static bool IsHexadecimalDigit(char c) {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        /// <summary>
        /// Consumes a run of digits, appending them to <paramref name="digits"/>. When
        /// <paramref name="allowUnderscore"/> is set an '_' may appear between two digits of the
        /// run, so "1_0" scans but "1__0", "_1" and "1_" do not. Returns the number of digits
        /// consumed, or -1 for a misplaced underscore.
        /// </summary>
        private static int ScanDigitRun(string/*!*/ str, int end, ref int index, StringBuilder/*!*/ digits, bool hexadecimal, bool allowUnderscore) {
            int count = 0;
            while (index < end) {
                char c = str[index];
                if (hexadecimal ? IsHexadecimalDigit(c) : IsDecimalDigit(c)) {
                    digits.Append(c);
                    count++;
                    index++;
                } else if (c == '_' && allowUnderscore && count > 0) {
                    char next = (index + 1 < end) ? str[index + 1] : '\0';
                    if (!(hexadecimal ? IsHexadecimalDigit(next) : IsDecimalDigit(next))) {
                        return -1;
                    }
                    index++;
                } else {
                    break;
                }
            }
            return count;
        }

        /// <summary>Scans "[eEpP] [+-] digits"; returns false if no digits follow.</summary>
        private static bool ScanExponent(string/*!*/ str, int end, ref int index, out int exponent) {
            exponent = 0;

            bool negative = false;
            if (index < end && (str[index] == '+' || str[index] == '-')) {
                negative = (str[index] == '-');
                index++;
            }

            StringBuilder digits = new StringBuilder();
            if (ScanDigitRun(str, end, ref index, digits, false, true) <= 0) {
                return false;
            }

            if (!Int32.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out exponent)) {
                // Far past the point where the result saturates to 0 or Infinity either way.
                exponent = 99999;
            }
            if (negative) {
                exponent = -exponent;
            }
            return true;
        }

        public static bool TryParseRubyFloat(string/*!*/ str, out double result) {
            result = 0.0;

            int index = 0;
            int end = str.Length;
            while (index < end && IsFloatWhitespace(str[index])) {
                index++;
            }
            while (end > index && IsFloatWhitespace(str[end - 1])) {
                end--;
            }
            if (index == end) {
                return false;
            }

            bool negative = false;
            if (str[index] == '+' || str[index] == '-') {
                negative = (str[index] == '-');
                index++;
            }

            bool parsed = (index + 1 < end && str[index] == '0' && (str[index + 1] == 'x' || str[index + 1] == 'X'))
                ? TryParseHexadecimalFloat(str, index + 2, end, out result)
                : TryParseDecimalFloat(str, index, end, out result);

            if (parsed && negative) {
                result = -result;
            }
            return parsed;
        }

        private static bool TryParseDecimalFloat(string/*!*/ str, int index, int end, out double result) {
            result = 0.0;

            StringBuilder digits = new StringBuilder();
            int integerDigits = ScanDigitRun(str, end, ref index, digits, false, true);
            if (integerDigits < 0) {
                return false;
            }

            int scale = 0;
            if (index < end && str[index] == '.') {
                index++;
                int fractionDigits = ScanDigitRun(str, end, ref index, digits, false, true);
                if (fractionDigits < 0) {
                    return false;
                }
                if (fractionDigits == 0) {
                    // Ruby 3.4 accepts a point with nothing after it, so Float("10.") is 10.0 and
                    // Float("1.e5") is 100000.0, but there has to be something before it: "." and
                    // ".5e1" are still errors.
                    if (integerDigits == 0) {
                        return false;
                    }
                }
                scale = -fractionDigits;
            } else if (integerDigits == 0) {
                return false;
            }

            int exponent = 0;
            if (index < end && (str[index] == 'e' || str[index] == 'E')) {
                index++;
                if (!ScanExponent(str, end, ref index, out exponent)) {
                    return false;
                }
            }

            if (index != end) {
                return false;
            }

            long power = (long)exponent + scale;
            if (power > 99999) {
                power = 99999;
            } else if (power < -99999) {
                power = -99999;
            }

            // Double.Parse saturates to Infinity or zero rather than failing, which is what Ruby
            // does with "1e400" and "1e-400".
            return Double.TryParse(
                digits.ToString() + "E" + power.ToString(CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryParseHexadecimalFloat(string/*!*/ str, int index, int end, out double result) {
            result = 0.0;

            // '_' may separate two hexadecimal digits, exactly as in the decimal form.
            StringBuilder digits = new StringBuilder();
            int integerDigits = ScanDigitRun(str, end, ref index, digits, true, true);
            if (integerDigits < 0) {
                return false;
            }

            int scale = 0;
            if (index < end && str[index] == '.') {
                index++;
                int fractionDigits = ScanDigitRun(str, end, ref index, digits, true, true);
                if (fractionDigits <= 0) {
                    return false;
                }
                scale = -4 * fractionDigits;
            } else if (integerDigits == 0) {
                return false;
            }

            int exponent = 0;
            if (index < end && (str[index] == 'p' || str[index] == 'P')) {
                index++;
                if (!ScanExponent(str, end, ref index, out exponent)) {
                    return false;
                }
            }

            if (index != end) {
                return false;
            }

            BigInteger mantissa = BigInteger.Zero;
            for (int i = 0; i < digits.Length; i++) {
                char c = digits[i];
                int digit = (c <= '9') ? (c - '0') : ((c | 0x20) - 'a' + 10);
                mantissa = mantissa * 16 + digit;
            }

            long power = (long)exponent + scale;
            if (power > 99999) {
                power = 99999;
            } else if (power < -99999) {
                power = -99999;
            }

            result = Math.ScaleB((double)mantissa, (int)power);
            return true;
        }

        #endregion

        [Emitted] // ProtocolConversionAction
        public static Exception/*!*/ CreateTypeConversionError(string/*!*/ fromType, string/*!*/ toType) {
            return RubyExceptions.CreateTypeConversionError(fromType, toType);
        }

        [Emitted] // ProtocolConversionAction
        public static Exception/*!*/ CreateImplicitConversionError(string/*!*/ fromType, string/*!*/ toType) {
            return RubyExceptions.CreateImplicitConversionError(fromType, toType);
        }

        [Emitted] // ConvertToRegexAction
        public static Exception/*!*/ CreateWrongArgumentTypeError(string/*!*/ fromType, string/*!*/ toType) {
            return RubyExceptions.CreateTypeError("wrong argument type {0} (expected {1})",
                RubyExceptions.MessageTypeName(fromType), toType);
        }

        /// <summary>
        /// A method name that is neither a Symbol nor a String gets its own wording in MRI, naming
        /// the value rather than its class: "[] is not a symbol nor a string". Kernel#send,
        /// #public_send, #respond_to? and Module#instance_method all report it that way.
        /// </summary>
        [Emitted]
        public static Exception/*!*/ CreateNotSymbolNorStringError(RubyContext/*!*/ context, object obj) {
            return RubyExceptions.CreateTypeError("{0} is not a symbol nor a string", context.Inspect(obj).ToAsciiString());
        }

        /// <summary>
        /// The same message where only the argument's class is known -- the overload resolver
        /// rejects nil and friends without holding on to the value.
        /// </summary>
        [Emitted]
        public static Exception/*!*/ CreateNotSymbolNorStringErrorFor(string/*!*/ description) {
            return RubyExceptions.CreateTypeError("{0} is not a symbol nor a string", description);
        }

        [Emitted] // ConvertToFixnumAction
        public static int ConvertBignumToFixnum(BigInteger/*!*/ bignum) {
            int fixnum;
            if (bignum.AsInt32(out fixnum)) {
                return fixnum;
            }
            throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long'");
        }

        [Emitted] // Converter.ExplicitConvert
        public static int ConvertInt64ToFixnum(long value) {
            if (value >= Int32.MinValue && value <= Int32.MaxValue) {
                return (int)value;
            }
            // Same wording as the BigInteger case: which of the two CLR types happens to carry
            // the value is an implementation detail Ruby code must not be able to see.
            throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long'");
        }

        [Emitted] // ConvertDoubleToFixnum
        public static int ConvertDoubleToFixnum(double value) {
            try {
                return checked((int)value);
            } catch (OverflowException) {
                throw RubyExceptions.CreateRangeError(String.Format("float {0} out of range of integer", value));
            }
        }

        [Emitted] // ConvertToSAction
        public static MutableString/*!*/ ToSDefaultConversion(RubyContext/*!*/ context, object target, object converted) {
            return converted as MutableString ?? RubyUtils.ObjectToMutableString(context, target);
        }

        #endregion
        
        #region Instance variable support

        // `@x`: a Ruby object whose shape the site has seen is a reference compare and an array
        // load. See RubyInstanceData for the layout and why no lock is needed.
        [Emitted]
        public static object GetInstanceVariable(RubyScope/*!*/ scope, object self, InstanceVariableSite/*!*/ site) {
            RubyInstanceData data;
            var obj = self as RubyObject;
            if (obj != null) {
                data = obj._instanceData;
            } else {
                // class-level @x
                var module = self as RubyModule;
                if (module == null) {
                    return GetInstanceVariableSlow(scope.RubyContext, self, site);
                }
                if (module.Context.RactorsStarted && !site.IsCoreLibrary && RactorIsolation.IsNonMain(module.Context)) {
                    return GetModuleInstanceVariableChecked(module, site);
                }
                data = module._instanceData;
            }

            if (data == null) {
                return null;
            }
            object[] slots = data._ivars;
            var entry = site.Cache;
            if (RubyInstanceData.ShapeOf(slots) == entry.Shape) {
                return RubyInstanceData.GetSlot(slots, entry.Index);
            }
            return data.GetInstanceVariable(site);
        }

        public static object ReadInstanceVariable(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            RubyInstanceData data;
            var obj = self as RubyObject;
            if (obj != null) {
                data = obj._instanceData;
            } else {
                // class-level @x
                var module = self as RubyModule;
                if (module == null) {
                    return GetInstanceVariableSlow(context, self, site);
                }
                if (module.Context.RactorsStarted && !site.IsCoreLibrary && RactorIsolation.IsNonMain(module.Context)) {
                    return GetModuleInstanceVariableChecked(module, site);
                }
                data = module._instanceData;
            }

            if (data == null) {
                return null;
            }
            object[] slots = data._ivars;
            var entry = site.Cache;
            if (RubyInstanceData.ShapeOf(slots) == entry.Shape) {
                return RubyInstanceData.GetSlot(slots, entry.Index);
            }
            return data.GetInstanceVariable(site);
        }

        /// <summary>
        /// A class-level `@x` once the runtime has started Ractors: a non-main Ractor may only
        /// read a shareable value (RactorIsolation).
        /// </summary>
        private static object GetModuleInstanceVariableChecked(RubyModule/*!*/ module, InstanceVariableSite/*!*/ site) {
            RubyInstanceData data = module._instanceData;
            object value = (data != null) ? data.GetInstanceVariable(site) : null;
            return RactorIsolation.CheckInstanceVariableRead(module.Context, module, site.Name, value);
        }

        private static object GetInstanceVariableSlow(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            if (context.RactorsStarted && !site.IsCoreLibrary) {
                RactorIsolation.CheckInstanceVariableRead(context, self, site.Name, null);
            }
            var owner = self as IRubyInstanceDataOwner;
            RubyInstanceData data;
            if (owner == null || !owner.TryGetInstanceData(context, out data)) {
                data = context.TryGetInstanceData(self);
            }
            if (data == null) {
                return null;
            }
            object[] slots = data._ivars;
            var entry = site.Cache;
            if (RubyInstanceData.ShapeOf(slots) == entry.Shape) {
                return RubyInstanceData.GetSlot(slots, entry.Index);
            }
            return data.GetInstanceVariable(site);
        }

        [Emitted]
        public static bool IsDefinedInstanceVariable(RubyScope/*!*/ scope, object self, string/*!*/ name) {
            RubyInstanceData data = scope.RubyContext.TryGetInstanceData(self);
            if (data == null) return false;
            object value;
            return data.TryGetInstanceVariable(name, out value);
        }

        // `@x = v`: a Ruby object whose shape the site has seen stores without a lock; adding a
        // variable the site has seen added is a CAS. See RubyInstanceData.
        [Emitted]
        public static object SetInstanceVariable(object self, object value, RubyScope/*!*/ scope, InstanceVariableSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj != null) {
                var data = obj._instanceData ?? GetInstanceData(ref obj._instanceData);
                if (!data._frozen && data.TrySetCached(site.Cache, value, obj)) {
                    return value;
                }
            } else {
                var context = scope.RubyContext;
                if (context.RactorsStarted && !site.IsCoreLibrary) {
                    RactorIsolation.CheckInstanceVariableWrite(context, self);
                }
                if (TrySetOwnedInstanceVariable(context, self, value, site)) {
                    return value;
                }
            }
            scope.RubyContext.SetInstanceVariable(self, value, site);
            return value;
        }

        /// <summary>
        /// An attribute writer (attr_writer, attr_accessor): <see cref="SetInstanceVariable"/> of a value that is a positional
        /// argument, even a Hash passed as keywords (o.send(:x=, k: 1)) - kept, it stops saying so (<see cref="ClearKeywordArguments"/>).
        /// Here rather than in the writer's rule, which is interpreted for its first calls.
        /// </summary>
        [Emitted]
        public static object SetAttribute(object self, object value, RubyScope/*!*/ scope, InstanceVariableSite/*!*/ site) {
            return SetInstanceVariable(self, ClearKeywordArguments(value), scope, site);
        }

        // The cached store of a CLR object that keeps its own instance data (IRubyInstanceDataOwner);
        // the slow path - the frozen error among the rest - is the caller's when this says no.
        private static bool TrySetOwnedInstanceVariable(RubyContext/*!*/ context, object self, object value, InstanceVariableSite/*!*/ site) {
            var owner = self as IRubyInstanceDataOwner;
            if (owner == null) {
                return false;
            }
            var data = owner.GetInstanceData(context);
            return data != null && !data._frozen && data.TrySetCached(site.Cache, value, data.ImmediateClass);
        }

        public static object WriteInstanceVariable(RubyContext/*!*/ context, object self, object value, InstanceVariableSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj != null) {
                var data = obj._instanceData ?? GetInstanceData(ref obj._instanceData);
                if (!data._frozen && data.TrySetCached(site.Cache, value, obj)) {
                    return value;
                }
            } else {
                if (context.RactorsStarted && !site.IsCoreLibrary) {
                    RactorIsolation.CheckInstanceVariableWrite(context, self);
                }
                if (TrySetOwnedInstanceVariable(context, self, value, site)) {
                    return value;
                }
            }
            context.SetInstanceVariable(self, value, site);
            return value;
        }

        #endregion

        #region Class Variables

        /// <summary>
        /// The module whose class variables @@x names: the innermost class or module body. MRI
        /// refuses the access where there is none - the top level, a method or block defined there.
        /// </summary>
        private static RubyModule/*!*/ GetClassVariableOwner(RubyScope/*!*/ scope, string/*!*/ name) {
            RubyModule owner = scope.GetInnerMostModuleForClassVariableAccess();
            if (owner == null) {
                throw new RuntimeError("class variable access from toplevel");
            }
            if (owner.Context.RactorsStarted) {
                RactorIsolation.CheckClassVariable(owner, name);
            }
            return owner;
        }

        [Emitted]
        public static object GetClassVariable(RubyScope/*!*/ scope, string/*!*/ name) {
            // owner is the first module in scope:
            RubyModule owner = GetClassVariableOwner(scope, name);
            return GetClassVariableInternal(owner, name);
        }

        private static object GetClassVariableInternal(RubyModule/*!*/ module, string/*!*/ name) {
            object value;
            if (module.TryResolveClassVariable(name, out value) == null) {
                throw RubyExceptions.WithNameAndReceiver(module.Context,
                    RubyExceptions.CreateNameError(String.Format("uninitialized class variable {0} in {1}", name, module.Name)),
                    name, module);
            }
            return value;
        }

        [Emitted]
        public static object TryGetClassVariable(RubyScope/*!*/ scope, string/*!*/ name) {
            object value;
            // owner is the first module in scope:
            GetClassVariableOwner(scope, name).TryResolveClassVariable(name, out value);
            return value;
        }

        [Emitted]
        public static bool IsDefinedClassVariable(RubyScope/*!*/ scope, string/*!*/ name) {
            // owner is the first module in scope:
            RubyModule owner = scope.GetInnerMostModuleForClassVariableLookup();
            if (owner.Context.RactorsStarted) {
                RactorIsolation.CheckClassVariable(owner, name);
            }
            object value;
            return owner.TryResolveClassVariable(name, out value) != null;
        }

        [Emitted]
        public static object SetClassVariable(object value, RubyScope/*!*/ scope, string/*!*/ name) {
            return SetClassVariableInternal(GetClassVariableOwner(scope, name), name, value);
        }

        private static object SetClassVariableInternal(RubyModule/*!*/ lexicalOwner, string/*!*/ name, object value) {
            object oldValue;
            RubyModule owner = lexicalOwner.TryResolveClassVariable(name, out oldValue);
            (owner ?? lexicalOwner).SetClassVariable(name, value);
            return value;
        }

        #endregion

        #region Ruby Types

        [Emitted]
        public static string/*!*/ ObjectToString(IRubyObject/*!*/ obj) {
            return RubyUtils.ObjectToMutableString(obj).ToString();
        }

        [Emitted] //RubyTypeBuilder
        public static RubyInstanceData/*!*/ GetInstanceData(ref RubyInstanceData/*!*/ instanceData) {
            if (instanceData == null) {
                Interlocked.CompareExchange(ref instanceData, new RubyInstanceData(), null);
            }
            return instanceData;
        }

        [Emitted]
        public static bool IsObjectFrozen(RubyInstanceData instanceData) {
            return instanceData != null && instanceData.IsFrozen;
        }

        [Emitted]
        public static void FreezeObject(ref RubyInstanceData instanceData) {
            RubyOps.GetInstanceData(ref instanceData).Freeze();
        }

        [Emitted(UseReflection = true)] //RubyTypeBuilder
        public static void DeserializeObject(out RubyInstanceData/*!*/ instanceData, out RubyClass/*!*/ immediateClass, SerializationInfo/*!*/ info) {
            immediateClass = (RubyClass)info.GetValue(RubyUtils.SerializationInfoClassKey, typeof(RubyClass));
            RubyInstanceData newInstanceData = null;
            foreach (SerializationEntry entry in info) {
                if (entry.Name.StartsWith("@", StringComparison.Ordinal)) {
                    if (newInstanceData == null) {
                        newInstanceData = new RubyInstanceData();
                    }
                    newInstanceData.SetInstanceVariable(entry.Name, entry.Value);
                }
            }
            instanceData = newInstanceData;
        }

        [Emitted(UseReflection = true)] //RubyTypeBuilder
        public static void SerializeObject(RubyInstanceData instanceData, RubyClass/*!*/ immediateClass, SerializationInfo/*!*/ info) {
            info.AddValue(RubyUtils.SerializationInfoClassKey, immediateClass, typeof(RubyClass));
            if (instanceData != null) {
                string[] instanceNames = instanceData.GetInstanceVariableNames();
                foreach (string name in instanceNames) {
                    object value;
                    if (!instanceData.TryGetInstanceVariable(name, out value)) {
                        value = null;
                    }
                    info.AddValue(name, value, typeof(object));
                }
            }
        }
        #endregion

        #region Delegates, Events

        /// <summary>
        /// Hooks up an event to call a proc at hand.
        /// EventInfo is passed in as object since it is an internal type.
        /// </summary>
        [Emitted]
        public static Proc/*!*/ HookupEvent(RubyEventInfo/*!*/ eventInfo, object/*!*/ target, Proc/*!*/ proc) {
            eventInfo.Tracker.AddHandler(target, proc, eventInfo.Context.DelegateCreator);
            return proc;
        }

        [Emitted]
        public static RubyEvent/*!*/ CreateEvent(RubyEventInfo/*!*/ eventInfo, object/*!*/ target, string/*!*/ name) {
            return new RubyEvent(target, eventInfo, name);
        }

        [Emitted]
        public static Delegate/*!*/ CreateDelegateFromProc(Type/*!*/ type, Proc proc) {
            if (proc == null) {
                throw RubyExceptions.NoBlockGiven();
            }
            // Each invocation calls the proc the way Proc#call does, with its own flow-control frame, so
            // a break in a block passed to a CLR method as a delegate unwinds to that call, and a
            // return in a lambda returns from the lambda.
            return proc.LocalScope.RubyContext.DelegateCreator.GetDelegate(proc, type);
        }

        [Emitted]
        public static Delegate/*!*/ CreateDelegateFromMethod(Type/*!*/ type, RubyMethod/*!*/ method) {
            return method.Info.Context.DelegateCreator.GetDelegate(method, type);
        }

        #endregion

        #region Tuples

        // Instance variable storages needs MT<n> to be a subclass of MT<m> for all n > m.
        // This property is not true if we used DynamicNull as a generic argument for arities that are not powers of 2 like MutableTuple.MakeTupleType does.
        // We make this property true for all simple tuples, thus instance variable storages can only use tuples of size <= 128.
        internal static Type/*!*/ MakeObjectTupleType(int fieldCount) {
            if (fieldCount <= MutableTuple.MaxSize) {
                if (fieldCount <= 1) {
                    return typeof(MutableTuple<object>);
                } else if (fieldCount <= 2) {
                    return typeof(MutableTuple<object, object>);
                } else if (fieldCount <= 4) {
                    return typeof(MutableTuple<object, object, object, object>);
                } else if (fieldCount <= 8) {
                    return typeof(MutableTuple<object, object, object, object, object, object, object, object>);
                } else if (fieldCount <= 16) {
                    return typeof(MutableTuple<object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object>);
                } else if (fieldCount <= 32) {
                    return typeof(MutableTuple<object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object>);
                } else if (fieldCount <= 64) {
                    return typeof(MutableTuple<object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object>);
                } else {
                    return MakeObjectTupleType128();
                }
            }

            Type[] types = new Type[fieldCount];
            for (int i = 0; i < types.Length; i++) {
                types[i] = typeof(object);
            }
            return MutableTuple.MakeTupleType(types);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Type/*!*/ MakeObjectTupleType128() {
            return typeof(MutableTuple<object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object, object>);
        }

        #endregion

        [Emitted]
        public static void X(string marker) {
        }
        
        /// <summary>
        /// The constructor of a value type that declares no parameterless one: T.new is default(T).
        /// Instantiated for the type, it returns T, so keywords passed to new can set the new value's
        /// fields and properties like those passed to a real constructor. (Not constrained to struct: Nullable&lt;T&gt;
        /// is a value type that does not satisfy that constraint, and System::Nullable[Integer].new is nil.)
        /// </summary>
        public static T CreateDefaultValue<T>() {
            return default(T);
        }

        internal static readonly MethodInfo/*!*/ CreateDefaultValueMethod = typeof(RubyOps).GetMethod(nameof(CreateDefaultValue));

        // A Ruby Array or Hash passed where a CLR method takes a typed collection (see Converter.TryConvertToCollection):
        // a copy, each item converted by the site the binder made for the item type.

        public static T[]/*!*/ ConvertToVector<T>(IList/*!*/ list, CallSite<Func<CallSite, object, T>>/*!*/ itemConversion) {
            var result = new T[list.Count];
            for (int i = 0; i < result.Length; i++) {
                result[i] = itemConversion.Target(itemConversion, list[i]);
            }
            return result;
        }

        public static List<T>/*!*/ ConvertToList<T>(IList/*!*/ list, CallSite<Func<CallSite, object, T>>/*!*/ itemConversion) {
            var result = new List<T>(list.Count);
            for (int i = 0; i < list.Count; i++) {
                result.Add(itemConversion.Target(itemConversion, list[i]));
            }
            return result;
        }

        public static Dictionary<TKey, TValue>/*!*/ ConvertToDictionary<TKey, TValue>(IDictionary<object, object>/*!*/ hash,
            CallSite<Func<CallSite, object, TKey>>/*!*/ keyConversion, CallSite<Func<CallSite, object, TValue>>/*!*/ valueConversion) {

            var result = new Dictionary<TKey, TValue>(hash.Count);
            foreach (var entry in hash) {
                result[keyConversion.Target(keyConversion, entry.Key)] = valueConversion.Target(valueConversion, entry.Value);
            }
            return result;
        }

        /// <summary>
        /// The module or class of a type group's non-generic member, where a type group is passed for a module
        /// (see Converter.TryConvertTypeGroup).
        /// </summary>
        public static RubyModule/*!*/ ConvertTypeGroupToModule(RubyContext/*!*/ context, Microsoft.Scripting.Actions.TypeGroup/*!*/ typeGroup) {
            Type type;
            if (!typeGroup.TryGetNonGenericType(out type)) {
                throw RubyExceptions.CreateTypeError(String.Format("{0} has no non-generic type to stand for a module; use {0}[...] or {0}.of(...)",
                    typeGroup.Name));
            }
            return context.GetModule(type);
        }

        /// <summary>
        /// A Ruby index passed for a System.Index: a negative one counts from the end, -1 is ^1.
        /// </summary>
        public static System.Index ConvertToIndex(int index) {
            return index < 0 ? System.Index.FromEnd(-index) : System.Index.FromStart(index);
        }

        /// <summary>
        /// A Ruby Range of Integers passed for a System.Range. Ruby's end is inclusive unless the range excludes it,
        /// and either end may be nil (beginless, endless): 1..3 is 1..4, 1...-1 is 1..^1, -3..-1 is ^3..^0.
        /// </summary>
        public static System.Range ConvertToRange(Range/*!*/ range) {
            System.Index start = range.Begin == null ? System.Index.Start : ConvertToIndex(GetRangeEndpoint(range.Begin));
            System.Index end;
            if (range.End == null) {
                end = System.Index.End;
            } else {
                int last = GetRangeEndpoint(range.End);
                if (range.ExcludeEnd) {
                    end = ConvertToIndex(last);
                } else {
                    end = last >= 0 ? System.Index.FromStart(last + 1) : System.Index.FromEnd(-last - 1);
                }
            }
            return new System.Range(start, end);
        }

        private static int GetRangeEndpoint(object value) {
            if (value is int) {
                return (int)value;
            }
            throw RubyExceptions.CreateTypeError(String.Format("no implicit conversion of {0} into System::Range (Integer endpoints expected)",
                value == null ? "nil" : value.GetType().Name));
        }

        #region Constructing instances of Ruby classes derived from CLR classes

        // Instances new allocated without running a constructor - their CLR base class has no parameterless one -
        // that initialize has yet to construct by calling super.
        private static readonly ConditionalWeakTable<object, object>/*!*/ _unconstructedInstances = new ConditionalWeakTable<object, object>();

        /// <summary>
        /// Allocates an instance of the CLR type emitted for a Ruby class derived from a CLR class that has no
        /// parameterless constructor, without running any constructor. The Ruby class's initialize constructs it:
        /// its super goes to the base class's constructor (see InvokeClrConstructor).
        /// </summary>
        public static object/*!*/ AllocateUnconstructed(RubyClass/*!*/ rubyClass, Type/*!*/ type) {
            var instance = (IRubyObject)RuntimeHelpers.GetUninitializedObject(type);
            instance.ImmediateClass = rubyClass;
            _unconstructedInstances.AddOrUpdate(instance, null);
            return instance;
        }

        /// <summary>
        /// The result of new for an instance allocated by AllocateUnconstructed, once initialize has returned: an
        /// error if initialize did not construct it, which C# would not have compiled.
        /// </summary>
        public static object EnsureConstructed(object result, Type/*!*/ baseType) {
            object unused;
            if (result != null && _unconstructedInstances.TryGetValue(result, out unused)) {
                _unconstructedInstances.Remove(result);
                throw RubyExceptions.CreateTypeError(String.Format(
                    "{0}.new: initialize did not call super, and {1} has no parameterless constructor to construct the instance with",
                    ((IRubyObject)result).ImmediateClass.NominalClass.Name, baseType.Name));
            }
            return result;
        }

        /// <summary>
        /// super in initialize of a Ruby class derived from a CLR class: runs the base class's constructor on the
        /// instance new has allocated. The parameterless constructor has already run if there is one, as allocation
        /// uses it, so calling it again does nothing.
        /// </summary>
        public static object/*!*/ InvokeClrConstructor(ConstructorInfo/*!*/ constructor, object/*!*/ instance, object[]/*!*/ args) {
            object unused;
            bool unconstructed = _unconstructedInstances.TryGetValue(instance, out unused);
            if (args.Length == 0 && !unconstructed) {
                return instance;
            }

            try {
                constructor.Invoke(instance, args);
            } catch (TargetInvocationException e) {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw();
            }

            if (unconstructed) {
                _unconstructedInstances.Remove(instance);
            }
            return instance;
        }

        /// <summary>
        /// An argument passed for a CLR method's Object parameter: a Ruby String as a System.String (see
        /// RubyOverloadResolver.Convert), anything else as it is.
        /// </summary>
        public static object ToClrArgument(object value) {
            var str = value as MutableString;
            return (str != null) ? str.ConvertToString() : value;
        }

        /// <summary>
        /// The test of a rule whose overload was chosen by the items of a Ruby Array argument (see
        /// RubyOverloadResolver.SelectBestItemConversion): whether <paramref name="itemTypes"/> are the distinct CLR types of
        /// the items, typeof(DynamicNull) for nil - each item has one of them, and each is some item's. Linear in the items,
        /// as is the conversion of the Array that follows.
        /// </summary>
        public static bool HasItemTypes(IList/*!*/ list, Type/*!*/[]/*!*/ itemTypes) {
            Span<bool> seen = itemTypes.Length <= 32 ? stackalloc bool[itemTypes.Length] : new bool[itemTypes.Length];
            int seenCount = 0;
            for (int i = 0; i < list.Count; i++) {
                object item = list[i];
                int index = Array.IndexOf(itemTypes, (item != null) ? item.GetType() : typeof(DynamicNull));
                if (index < 0) {
                    return false;
                }
                if (!seen[index]) {
                    seen[index] = true;
                    seenCount++;
                }
            }
            return seenCount == itemTypes.Length;
        }

        internal static readonly MethodInfo/*!*/ HasItemTypesMethod = typeof(RubyOps).GetMethod(nameof(HasItemTypes));
        internal static readonly MethodInfo/*!*/ ToClrArgumentMethod = typeof(RubyOps).GetMethod(nameof(ToClrArgument));
        internal static readonly MethodInfo/*!*/ AllocateUnconstructedMethod = typeof(RubyOps).GetMethod(nameof(AllocateUnconstructed));
        internal static readonly MethodInfo/*!*/ EnsureConstructedMethod = typeof(RubyOps).GetMethod(nameof(EnsureConstructed));
        internal static readonly MethodInfo/*!*/ InvokeClrConstructorMethod = typeof(RubyOps).GetMethod(nameof(InvokeClrConstructor));

        #endregion

        internal static readonly MethodInfo/*!*/ ConvertToIndexMethod = typeof(RubyOps).GetMethod(nameof(ConvertToIndex));
        internal static readonly MethodInfo/*!*/ ConvertToRangeMethod = typeof(RubyOps).GetMethod(nameof(ConvertToRange));
        internal static readonly MethodInfo/*!*/ ConvertTypeGroupToModuleMethod = typeof(RubyOps).GetMethod(nameof(ConvertTypeGroupToModule));
        internal static readonly MethodInfo/*!*/ ConvertToVectorMethod = typeof(RubyOps).GetMethod(nameof(ConvertToVector));
        internal static readonly MethodInfo/*!*/ ConvertToListMethod = typeof(RubyOps).GetMethod(nameof(ConvertToList));
        internal static readonly MethodInfo/*!*/ ConvertToDictionaryMethod = typeof(RubyOps).GetMethod(nameof(ConvertToDictionary));
    }
}

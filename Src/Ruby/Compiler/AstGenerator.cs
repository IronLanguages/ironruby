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
using MSA = System.Linq.Expressions;
#else
using MSA = Microsoft.Scripting.Ast;
#endif

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Utils;
    
namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstBlock = Microsoft.Scripting.Ast.BlockBuilder;
    using AstExpressions = ReadOnlyCollectionBuilder<MSA.Expression>;
    
    internal sealed class AstGenerator {
        private static int _UniqueId;

        private readonly RubyContext/*!*/ _context;
        private readonly RubyCompilerOptions/*!*/ _compilerOptions;
        private readonly MSA.SymbolDocumentInfo _document;
        private readonly MSA.Expression _sequencePointClearance;
        private readonly RubyEncoding/*!*/ _encoding;
        private readonly Profiler _profiler;
        private readonly bool _printInteractiveResult;
        private readonly bool _debugCompiler;
        private readonly bool _debugMode;
        private readonly bool _traceEnabled;
        private readonly bool _traceable;
        private readonly bool _savingToDisk;

        private IList<MSA.Expression> _fileInitializers; // lazy
        private MSA.Expression _sourcePathConstant; // lazy

        internal AstGenerator(RubyContext/*!*/ context, RubyCompilerOptions/*!*/ options, MSA.SymbolDocumentInfo document, RubyEncoding/*!*/ encoding,
            bool printInteractiveResult) {

            Assert.NotNull(context, options, encoding);
            _context = context;
            _compilerOptions = options;
            _debugMode = context.DomainManager.Configuration.DebugMode;
            _traceEnabled = context.RubyOptions.EnableTracing;
            _document = document;
            _traceable = !TracePoint.IsCoreLibraryPath(SourcePath);
            _sequencePointClearance = (document != null) ? Ast.ClearDebugInfo(document) : null;
            _encoding = encoding;
            _profiler = context.RubyOptions.Profile ? Profiler.Instance : null;
            _savingToDisk = context.RubyOptions.SavePath != null;
            _printInteractiveResult = printInteractiveResult;
#if FEATURE_REFEMIT && FEATURE_FILESYSTEM
            _debugCompiler = Snippets.Shared.SaveSnippets;
#else
            _debugCompiler = false;
#endif
        }

        public RubyCompilerOptions/*!*/ CompilerOptions { 
            get { return _compilerOptions; } 
        }

        public bool DebugMode { 
            get { return _debugMode; }
        }

        public Profiler Profiler {
            get { return _profiler; }
        }

        public bool DebugCompiler {
            get { return _debugCompiler; }
        }

        public bool TraceEnabled {
            get { return _traceEnabled; }
        }

        /// <summary>
        /// False for the Ruby half of the core library: TracePoint does not see into it, as MRI does
        /// not see into its C core.
        /// </summary>
        public bool Traceable {
            get { return _traceable; }
        }

        /// <summary>
        /// -X:OSR - whether a loop may count its own back edges and swap itself for a
        /// type-specialized copy once it is hot. Off unless asked for, and off wherever the
        /// loop has to keep running the tree it was built from: TracePoint and coverage want
        /// an event per line, a saved assembly cannot carry the site, and eval'd code is
        /// never around long enough to be worth a compilation.
        /// </summary>
        public bool CanReplaceLoops {
            get {
                return _context.RubyOptions.Osr && !_traceEnabled && !_savingToDisk
                    && !_compilerOptions.IsEval && Coverage == null;
            }
        }

        public bool SavingToDisk {
            get { return _savingToDisk; }
        }

        public string SourcePath {
            get {
                if (_document == null) {
                    return "(eval)";
                }
                string path = _document.FileName;
                RubyUtils.DecodeEvalLineOffset(ref path);
                return path;
            }
        }

        public MSA.SymbolDocumentInfo Document {
            get { return _document; }
        }

        public bool PrintInteractiveResult {
            get { return _printInteractiveResult; }
        }

        public MSA.Expression/*!*/ SourcePathConstant {
            get {
                if (_sourcePathConstant == null) {
                    _sourcePathConstant = Ast.Constant(SourcePath, typeof(string));
                }
                return _sourcePathConstant;
            }
        }

        public RubyEncoding/*!*/ Encoding {
            get { return _encoding; }
        }

        internal RubyContext/*!*/ Context {
            get { return _context; }
        }

        internal void AddFileInitializer(MSA.Expression/*!*/ expression) {
            if (_fileInitializers == null) {
                _fileInitializers = new List<MSA.Expression>();
            }
            _fileInitializers.Add(expression);
        }

        internal IList<MSA.Expression>/*!*/ FileInitializers {
            get {
                return (IList<MSA.Expression>)_fileInitializers ?? AstFactory.EmptyExpressions;
            }
        }

        #region Lexical Scopes

        public abstract class LexicalScope {
            public LexicalScope Parent;
            public virtual bool IsLambda { get { return false; } }
        }

        public sealed class LoopScope : LexicalScope {
            private readonly MSA.Expression/*!*/ _redoVariable;
            private readonly MSA.Expression/*!*/ _resultVariable;
            private readonly MSA.LabelTarget/*!*/ _breakLabel;
            private readonly MSA.LabelTarget/*!*/ _continueLabel;
            public LoopScope _parentLoop;

            public MSA.Expression/*!*/ RedoVariable {
                get { return _redoVariable; }
            }

            public MSA.Expression/*!*/ ResultVariable {
                get { return _resultVariable; }
            }

            public MSA.LabelTarget/*!*/ BreakLabel {
                get { return _breakLabel; }
            }

            public MSA.LabelTarget/*!*/ ContinueLabel {
                get { return _continueLabel; }
            }

            public LoopScope ParentLoop {
                get { return _parentLoop; }
                set { _parentLoop = value; }
            }

            public LoopScope(MSA.Expression/*!*/ redoVariable, MSA.Expression/*!*/ resultVariable, MSA.LabelTarget/*!*/ breakLabel, MSA.LabelTarget/*!*/ continueLabel) {
                Assert.NotNull(redoVariable, resultVariable, breakLabel, continueLabel);
                _redoVariable = redoVariable;
                _resultVariable = resultVariable;
                _breakLabel = breakLabel;
                _continueLabel = continueLabel;
            }
        }

        public sealed class RescueScope : LexicalScope {
            private readonly MSA.Expression/*!*/ _retryingVariable;
            private readonly MSA.LabelTarget/*!*/ _retryLabel;

            public RescueScope _parentRescue;

            public MSA.Expression/*!*/ RetryingVariable {
                get { return _retryingVariable; }
            }

            public RescueScope ParentRescue {
                get { return _parentRescue; }
                set { _parentRescue = value; }
            }

            public MSA.LabelTarget/*!*/ RetryLabel {
                get { return _retryLabel; }
            }

            public RescueScope(MSA.Expression/*!*/ retryingVariable, MSA.LabelTarget/*!*/ retryLabel) {
                Assert.NotNull(retryingVariable, retryLabel);
                _retryingVariable = retryingVariable;
                _retryLabel = retryLabel;
            }
        }

        public class VariableScope : LexicalScope {
            private readonly ScopeBuilder/*!*/ _builder;
            private readonly MSA.Expression/*!*/ _selfVariable;
            private readonly MSA.ParameterExpression/*!*/ _runtimeScopeVariable;
            private VariableScope _parentVariableScope;

            // The line of the last statement given a TracePoint :line hook in this scope's code.
            internal int LastTracedLine;

            public ScopeBuilder/*!*/ Builder {
                get { return _builder; }
            }

            public MSA.Expression/*!*/ SelfVariable {
                get { return _selfVariable; }
            }

            public MSA.ParameterExpression/*!*/ RuntimeScopeVariable {
                get { return _runtimeScopeVariable; }
            }

            public VariableScope ParentVariableScope {
                get { return _parentVariableScope; }
                set { _parentVariableScope = value; }
            }

            public VariableScope(ScopeBuilder/*!*/ locals, MSA.Expression/*!*/ selfVariable, MSA.ParameterExpression/*!*/ runtimeScopeVariable) {
                Assert.NotNull(locals, selfVariable, runtimeScopeVariable);
                _builder = locals;
                _runtimeScopeVariable = runtimeScopeVariable;
                _selfVariable = selfVariable;
            }
        }

        public abstract class FrameScope : VariableScope {
            // A unique id of the scope per app-domain. Used for caching calls in dynamic sites.
            private readonly int _uniqueId;

            private BlockScope _parentBlock;
            private RescueScope _parentRescue;
            private LoopScope _parentLoop;
            private MSA.LabelTarget _returnLabel;

            public int UniqueId {
                get { return _uniqueId; }
            }
            
            public BlockScope ParentBlock { get { return _parentBlock; } set { _parentBlock = value; } }
            public RescueScope ParentRescue { get { return _parentRescue; } set { _parentRescue = value; } }
            public LoopScope ParentLoop { get { return _parentLoop; } set { _parentLoop = value; } }

            public FrameScope(ScopeBuilder/*!*/ builder, MSA.Expression/*!*/ selfVariable, MSA.ParameterExpression/*!*/ runtimeScopeVariable)
                : base(builder, selfVariable, runtimeScopeVariable) {
                _uniqueId = Interlocked.Increment(ref _UniqueId);
            }

            internal MSA.LabelTarget/*!*/ ReturnLabel {
                get {
                    if (_returnLabel == null) {
                        _returnLabel = MSA.Expression.Label(typeof(object));
                    }
                    return _returnLabel;
                }
            }

            internal MSA.Expression/*!*/ AddReturnTarget(MSA.Expression/*!*/ expression) {
                if (expression.Type != typeof(object)) {
                    expression = AstUtils.Convert(expression, typeof(object));
                }
                if (_returnLabel != null) {
                    expression = Ast.Label(_returnLabel, expression);
                    _returnLabel = null;
                }
                return expression;
            }
        }

        public sealed class BlockScope : FrameScope {
            private readonly MSA.Expression/*!*/ _bfcVariable;
            private readonly MSA.LabelTarget/*!*/ _redoLabel;

            public override bool IsLambda {
                get { return true; }
            }

            public MSA.Expression/*!*/ BfcVariable {
                get { return _bfcVariable; }
            }

            public MSA.LabelTarget/*!*/ RedoLabel {
                get { return _redoLabel; }
            }

            // A block nested in this one has a return statement: if this block runs as a lambda,
            // that return leaves it, so its body catches the LambdaUnwinder.
            public bool HasNestedReturn;

            // A block literal (or lambda) is nested in this one: its Proc captures this block's scope.
            public bool HasNestedBlock;

            public BlockScope(ScopeBuilder/*!*/ builder, MSA.Expression/*!*/ selfVariable, MSA.ParameterExpression/*!*/ runtimeScopeVariable,
                MSA.Expression/*!*/ bfcVariable, MSA.LabelTarget/*!*/ redoLabel)
                : base(builder, selfVariable, runtimeScopeVariable) {
                Assert.NotNull(bfcVariable, redoLabel);
                _bfcVariable = bfcVariable;
                _redoLabel = redoLabel;
            }
        }

        public sealed class MethodScope : FrameScope {
            private readonly MSA.Expression _blockVariable;
            private readonly string _methodName;
            private readonly Parameters _parameters;
            private MethodScope _parentMethod;

            public override bool IsLambda {
                get { return true; }
            }

            // use MakeMethodBlockParameterRead to access method's block parameter:
            public MSA.Expression BlockVariable {
                get { return _blockVariable; }
            }

            public MethodScope ParentMethod {
                get { return _parentMethod; }
                set { _parentMethod = value; }
            }

            // null for top-level code, used by block definition (to encode method name into stack frame)
            public string MethodName {
                get { return _methodName; }
            }

            /// <summary>
            /// The name this method's frame carries in a backtrace: the same as MethodName, but
            /// qualified with the owner ("M::C#foo") once the owner is known - which is at
            /// definition time, when the body is compiled. Blocks inside the method read it to
            /// build their own "block in ..." labels.
            /// </summary>
            public string FrameLabel { get; set; }

            // null for code that is evaluated outside a method scope, used by super-call
            public Parameters Parameters {
                get { return _parameters; }
            }

            // Where an explicit `return' goes once it has reported TracePoint :return itself, past
            // the hook that reports the implicit return at `end'. Null until a `return' needs it.
            internal MSA.LabelTarget ExplicitReturnLabel { get; set; }

            public MethodScope(
                ScopeBuilder/*!*/ builder,
                MSA.Expression/*!*/ selfVariable,
                MSA.ParameterExpression/*!*/ runtimeScopeVariable,
                MSA.Expression blockVariable, 
                string methodName, 
                Parameters parameters)
                : base(builder, selfVariable, runtimeScopeVariable) {

                _blockVariable = blockVariable;
                _methodName = methodName;
                _parameters = parameters;
            }
        }

        public sealed class ModuleScope : VariableScope {
            private readonly bool _isSingleton;
            private ModuleScope _parentModule;

            // The label of the body's frame, and the block the definition is written in - blocks
            // inside the body count their nesting from the body, not from the enclosing method.
            public string FrameLabel { get; set; }
            public BlockScope OuterBlock { get; set; }

            public LoopScope OuterLoop { get; set; }

            // The body is a lambda of its own, so it cannot jump to a label outside it: `break' out of
            // the loop the definition is written in, `return' from a `class << self' inside a method, a
            // return a block call propagates. Such a jump leaves the body with a number saying which
            // one it was and the value it carried, and the code after the call makes it again - from
            // the enclosing frame, where it may have to escape another body the same way.
            private MSA.LabelTarget _returnLabel;
            private MSA.ParameterExpression _escapeCode;
            private MSA.ParameterExpression _escapeValue;
            private List<Func<AstGenerator, MSA.Expression, MSA.Expression>> _escapes;

            public ScopeBuilder OuterLocals { get; set; }

            internal MSA.Expression/*!*/ Escape(MSA.Expression/*!*/ value, Func<AstGenerator, MSA.Expression, MSA.Expression>/*!*/ outerJump) {
                if (_escapes == null) {
                    _escapes = new List<Func<AstGenerator, MSA.Expression, MSA.Expression>>();
                    _escapeCode = OuterLocals.DefineHiddenVariable("#escape", typeof(int));
                    _escapeValue = OuterLocals.DefineHiddenVariable("#escape-value", typeof(object));
                }
                _escapes.Add(outerJump);
                if (value.Type == typeof(void)) {
                    value = Ast.Block(value, AstUtils.Constant(null, typeof(object)));
                }
                return Ast.Block(
                    Ast.Assign(_escapeValue, AstUtils.Box(value)),
                    Ast.Assign(_escapeCode, AstUtils.Constant(_escapes.Count)),
                    Ast.Return(ReturnLabel, AstUtils.Constant(null, typeof(object))),
                    AstUtils.Empty()
                );
            }

            // Before the call: nothing has escaped yet.
            internal MSA.Expression/*!*/ ResetEscapes() {
                return _escapes != null ? (MSA.Expression)Ast.Assign(_escapeCode, AstUtils.Constant(0)) : AstUtils.Empty();
            }

            // After the call, in the enclosing frame: make the jump that escaped.
            internal MSA.Expression/*!*/ MakeEscapedJumps(AstGenerator/*!*/ gen) {
                if (_escapes == null) {
                    return AstUtils.Empty();
                }
                var jumps = new List<MSA.Expression>();
                for (int i = 0; i < _escapes.Count; i++) {
                    jumps.Add(AstUtils.IfThen(
                        Ast.Equal(_escapeCode, AstUtils.Constant(i + 1)),
                        _escapes[i](gen, _escapeValue)
                    ));
                }
                jumps.Add(AstUtils.Empty());
                return Ast.Block(jumps);
            }

            internal MSA.LabelTarget/*!*/ ReturnLabel {
                get {
                    if (_returnLabel == null) {
                        _returnLabel = MSA.Expression.Label(typeof(object));
                    }
                    return _returnLabel;
                }
            }

            internal MSA.Expression/*!*/ AddReturnTarget(MSA.Expression/*!*/ expression) {
                return _returnLabel != null ? Ast.Label(_returnLabel, expression) : expression;
            }

            public ModuleScope ParentModule {
                get { return _parentModule; }
                set { _parentModule = value; }
            }

            public bool IsSingleton {
                get { return _isSingleton; }
            }

            public ModuleScope(ScopeBuilder/*!*/ builder, MSA.Expression/*!*/ selfVariable, MSA.ParameterExpression/*!*/ runtimeScopeVariable, bool isSingleton)
                : base(builder, selfVariable, runtimeScopeVariable) {
                _isSingleton = isSingleton;
            }
        }

        private LexicalScope/*!*/ _currentElement;
        private MethodScope/*!*/ _currentMethod;
        private BlockScope _currentBlock;
        private LoopScope _currentLoop;
        private RescueScope _currentRescue;
        private VariableScope _currentVariableScope;
        private MethodScope _topLevelScope;

        // inner-most module (available only if we enter a module declaration in the current AST, not available in eval'd code or in a method):
        private ModuleScope _currentModule;

        // inner-most method or top-level frame:
        public MethodScope/*!*/ CurrentMethod {
            get {
                Debug.Assert(_currentMethod != null);
                return _currentMethod;
            }
        }

        // inner-most frame scope (block or method):
        public FrameScope/*!*/ CurrentFrame {
            get { return (FrameScope)CurrentBlock ?? CurrentMethod; }
        }

        // inner-most block scope within the current method frame:
        public BlockScope CurrentBlock {
            get { return _currentBlock; }
        }

        // inner-most loop within the current frame (block or method):
        public LoopScope CurrentLoop {
            get { return _currentLoop; }
        }

        // inner-most rescue within the current frame (block or method):
        public RescueScope CurrentRescue {
            get { return _currentRescue; }
        }

        // "self" variable of the current variable scope:
        public MSA.Expression/*!*/ CurrentSelfVariable {
            get { return _currentVariableScope.SelfVariable; }
        }

        // runtime scope variable of the current variable scope:
        public MSA.ParameterExpression/*!*/ CurrentScopeVariable {
            get { return _currentVariableScope.RuntimeScopeVariable; }
        }

        // inner-most scope builder:
        public ScopeBuilder/*!*/ CurrentScope {
            get { return _currentVariableScope.Builder; }
        }

        // top-level (source unit tree) method scope:
        public MethodScope TopLevelScope {
            get { return _topLevelScope; }
        }

        #endregion

        #region Entering and Leaving Lexical Scopes

        public void EnterLoop(MSA.Expression/*!*/ redoVariable, MSA.Expression/*!*/ resultVariable, MSA.LabelTarget/*!*/ breakLabel, MSA.LabelTarget/*!*/ continueLabel) {
            Assert.NotNull(redoVariable, resultVariable, breakLabel, continueLabel);

            LoopScope loop = new LoopScope(redoVariable, resultVariable, breakLabel, continueLabel);

            loop.Parent = _currentElement;
            loop.ParentLoop = _currentLoop;

            _currentElement = _currentLoop = loop;
        }

        public void LeaveLoop() {
            Debug.Assert(_currentElement == _currentLoop);
            _currentElement = _currentLoop.Parent;
            _currentLoop = _currentLoop.ParentLoop;
        }

        public void EnterRescueClause(MSA.Expression/*!*/ retryingVariable, MSA.LabelTarget/*!*/ retryLabel) {
            Assert.NotNull(retryingVariable, retryLabel);

            RescueScope body = new RescueScope(retryingVariable, retryLabel);

            body.Parent = _currentElement;
            body.ParentRescue = _currentRescue;

            _currentElement = _currentRescue = body;
        }

        public void LeaveRescueClause() {
            Debug.Assert(_currentElement == _currentRescue);
            _currentElement = _currentRescue.Parent;
            _currentRescue = _currentRescue.ParentRescue;
        }

        public void EnterBlockDefinition(
            ScopeBuilder/*!*/ locals,
            MSA.Expression/*!*/ bfcVariable,
            MSA.Expression/*!*/ selfVariable,
            MSA.ParameterExpression/*!*/ runtimeScopeVariable, 
            MSA.LabelTarget/*!*/ redoLabel) {
            Assert.NotNull(locals, bfcVariable, selfVariable);
            Assert.NotNull(redoLabel);

            BlockScope block = new BlockScope(locals, selfVariable, runtimeScopeVariable, bfcVariable, redoLabel);
            if (_currentBlock != null) {
                _currentBlock.HasNestedBlock = true;
            }
            block.Parent = _currentElement;
            block.ParentRescue = _currentRescue;
            block.ParentLoop = _currentLoop;
            block.ParentBlock = _currentBlock;
            block.ParentVariableScope = _currentVariableScope;
            
            _currentElement = block;
            _currentRescue = null;
            _currentLoop = null;
            _currentBlock = block;
            _currentVariableScope = block;
        }

        public void LeaveBlockDefinition() {
            Debug.Assert(_currentElement == _currentBlock);
            BlockScope oldBlock = _currentBlock;

            _currentElement = oldBlock.Parent;
            _currentRescue = oldBlock.ParentRescue;
            _currentLoop = oldBlock.ParentLoop;
            _currentVariableScope = oldBlock.ParentVariableScope;
            _currentBlock = oldBlock.ParentBlock;
        }

        public void EnterMethodDefinition(
            ScopeBuilder/*!*/ locals, 
            MSA.Expression/*!*/ selfParameter,
            MSA.ParameterExpression/*!*/ runtimeScopeVariable,
            MSA.Expression blockParameter,
            string/*!*/ methodName,
            Parameters parameters) {
            Assert.NotNull(locals, selfParameter, runtimeScopeVariable);

            MethodScope method = new MethodScope(
                locals,
                selfParameter, 
                runtimeScopeVariable, 
                blockParameter, 
                methodName, 
                parameters
            );

            method.Parent = _currentElement;
            method.ParentRescue = _currentRescue;
            method.ParentLoop = _currentLoop;
            method.ParentBlock = _currentBlock;
            method.ParentVariableScope = _currentVariableScope;
            method.ParentMethod = _currentMethod;

            _currentElement = method;
            _currentRescue = null;
            _currentLoop = null;
            _currentBlock = null;
            _currentVariableScope = method;
            _currentMethod = method;
        }

        public void LeaveMethodDefinition() {
            Debug.Assert(_currentElement == _currentMethod);
            MethodScope oldMethod = _currentMethod;

            _currentElement = oldMethod.Parent;
            _currentRescue = oldMethod.ParentRescue;
            _currentLoop = oldMethod.ParentLoop;
            _currentBlock = oldMethod.ParentBlock;
            _currentVariableScope = oldMethod.ParentVariableScope;
            _currentMethod = oldMethod.ParentMethod;
        }

        public void EnterModuleDefinition(
            ScopeBuilder/*!*/ locals,
            MSA.Expression/*!*/ selfVariable,
            MSA.ParameterExpression/*!*/ runtimeScopeVariable, 
            bool isSingleton,
            string frameLabel) {
            Assert.NotNull(locals, selfVariable, runtimeScopeVariable);

            ModuleScope module = new ModuleScope(locals, selfVariable, runtimeScopeVariable, isSingleton);
            module.FrameLabel = frameLabel;
            module.OuterBlock = _currentBlock;
            module.OuterLoop = _currentLoop;

            module.Parent = _currentElement;
            module.ParentVariableScope = _currentVariableScope;
            module.ParentModule = _currentModule;

            _currentElement = module;
            _currentVariableScope = module;
            _currentModule = module;
        }

        public void LeaveModuleDefinition() {
            Debug.Assert(_currentElement == _currentModule);
            ModuleScope oldModule = _currentModule;

            _currentElement = oldModule.Parent;
            _currentVariableScope = oldModule.ParentVariableScope;
            _currentModule = oldModule.ParentModule;
        }

        public void EnterFileInitializer(
            ScopeBuilder/*!*/ locals,
            MSA.Expression/*!*/ selfVariable,
            MSA.ParameterExpression/*!*/ runtimeScopeVariable) {

            VariableScope scope = new VariableScope(locals, selfVariable, runtimeScopeVariable);

            scope.Parent = _currentElement;
            scope.ParentVariableScope = _currentVariableScope;

            _currentElement = scope;
            _currentVariableScope = scope;
        }

        public void LeaveFileInitializer() {
            Debug.Assert(_currentElement == _currentVariableScope);
            VariableScope oldScope = _currentVariableScope;

            _currentElement = oldScope.Parent;
            _currentVariableScope = oldScope.ParentVariableScope;
        }

        /// <summary>
        /// The label a top-level frame carries in a backtrace. An eval keeps the anonymous
        /// name: MRI reports the enclosing frame's label there rather than one of its own.
        /// </summary>
        /// <summary>
        /// The class or module body the code being compiled is directly in - with no method
        /// definition in between - or null.
        /// </summary>
        internal ModuleScope GetEnclosingModuleFrame() {
            for (var element = _currentElement; element != null; element = element.Parent) {
                if (element is ModuleScope) {
                    return (ModuleScope)element;
                }
                if (element is MethodScope) {
                    return null;
                }
            }
            return null;
        }

        internal static string/*!*/ FormatBlockLabel(string/*!*/ label, int levels) {
            return levels == 0 ? label : levels == 1 ? "block in " + label : "block (" + levels + " levels) in " + label;
        }

        internal string/*!*/ TopLevelFrameLabel {
            get {
                switch (CompilerOptions.FactoryKind) {
                    case TopScopeFactoryKind.Main: return "<main>";
                    case TopScopeFactoryKind.File:
                    case TopScopeFactoryKind.WrappedFile: return "<top (required)>";
                    default:
                        if (CompilerOptions.EvalFrameBaseLabel != null) {
                            return FormatBlockLabel(CompilerOptions.EvalFrameBaseLabel, CompilerOptions.EvalFrameBlockLevels);
                        }
                        return RubyStackTraceBuilder.TopLevelMethodName;
                }
            }
        }

        public void EnterSourceUnit(
            ScopeBuilder/*!*/ locals,
            MSA.Expression/*!*/ selfParameter,
            MSA.ParameterExpression/*!*/ runtimeScopeVariable,
            MSA.Expression blockParameter,
            string methodName,
            Parameters parameters) {
            Assert.NotNull(locals, selfParameter, runtimeScopeVariable);

            Debug.Assert(_currentElement == null && _currentLoop == null && _currentRescue == null &&
                _currentVariableScope == null && _currentModule == null && _currentBlock == null && _currentMethod == null);

            EnterMethodDefinition(
                locals,
                selfParameter,
                runtimeScopeVariable,
                blockParameter,
                methodName,
                parameters);

            _topLevelScope = _currentMethod;
        }

        public void LeaveSourceUnit() {
            Debug.Assert(_currentElement == _currentMethod && _currentVariableScope == _currentMethod);
            Debug.Assert(_currentLoop == null && _currentRescue == null);
            Debug.Assert(_currentBlock == null);

            _currentElement = null;
            _currentMethod = null;
            _currentVariableScope = null;
            _topLevelScope = null;
        }

        #endregion

        /// <summary>
        /// Gets the inner most scope that compiles to a lambda expression.
        /// </summary>
        private VariableScope/*!*/ GetCurrentLambdaScope() {
            LexicalScope scope = _currentVariableScope;
            while (!scope.IsLambda) {
                scope = scope.Parent;
            }
            return (VariableScope)scope;
        }

        /// <summary>
        /// Makes a read of the current method's block parameter. 
        /// </summary>
        internal MSA.Expression/*!*/ MakeMethodBlockParameterRead() {
            VariableScope lambdaScope = GetCurrentLambdaScope();
            if (lambdaScope == CurrentMethod && CurrentMethod.BlockVariable != null) {
                return CurrentMethod.BlockVariable;
            } else {
                // TODO: we can optimize and inline for 1..n levels of nesting:
                return Methods.GetMethodBlockParameter.OpCall(CurrentScopeVariable);
            }
        }

        /// <summary>
        /// Makes a read of the Self property of the current method's block parameter. 
        /// Returns Null constant in top-level code.
        /// </summary>
        internal MSA.Expression/*!*/ MakeMethodBlockParameterSelfRead() {
            VariableScope lambdaScope = GetCurrentLambdaScope();
            if (lambdaScope == CurrentMethod && CurrentMethod.BlockVariable != null) {
                return Methods.GetProcSelf.OpCall(CurrentMethod.BlockVariable);
            } else {
                // TODO: we can optimize and inline for 1..n levels of nesting:
                return Methods.GetMethodBlockParameterSelf.OpCall(CurrentScopeVariable);
            }
        }

        internal AstExpressions/*!*/ TransformExpressions(IList<Expression>/*!*/ arguments) {
            Assert.NotNull(arguments);
            return TranformExpressions(arguments, new AstExpressions(arguments.Count));
        }

        internal AstExpressions/*!*/ TranformExpressions(IList<Expression>/*!*/ arguments, AstExpressions/*!*/ result) {
            Assert.NotNullItems(arguments);
            Assert.NotNull(result);

            foreach (Expression arg in arguments) {
                result.Add(arg.TransformRead(this));
            }

            return result;
        }

        internal MSA.Expression/*!*/ TransformStatements(Statements/*!*/ statements, ResultOperation resultOperation) {
            return TransformStatements(null, statements, null, resultOperation);
        }

        internal MSA.Expression/*!*/ TransformStatements(MSA.Expression prologue, Statements/*!*/ statements, ResultOperation resultOperation) {
            return TransformStatements(prologue, statements, null, resultOperation);
        }

        internal MSA.Expression/*!*/ TransformStatements(MSA.Expression prologue, Statements/*!*/ statements, MSA.Expression epilogue, 
            ResultOperation resultOperation) {

            Assert.NotNull(statements);

            // Every body nested in another - a block's, a loop's, a branch's - comes through here, and the
            // nesting can be deeper than a thread's stack goes (see StackGuard).
            if (!StackGuard.IsSufficient) {
                return StackGuard.RunOnFreshStack(() => TransformStatements(prologue, statements, epilogue, resultOperation));
            }

            int count = statements.Count + (prologue != null ? 1 : 0) + (epilogue != null ? 1 : 0);

            if (count == 0) {

                if (resultOperation.IsIgnore) {
                    return AstUtils.Empty();
                } else if (resultOperation.Variable != null) {
                    return Ast.Assign(resultOperation.Variable, AstUtils.Constant(null, resultOperation.Variable.Type));
                } else {
                    return Ast.Return(CurrentFrame.ReturnLabel, AstUtils.Constant(null));
                }

            } else if (count == 1) {
                if (prologue != null) {
                    return prologue;
                }

                if (epilogue != null) {
                    return epilogue;
                }

                if (resultOperation.IsIgnore) {
                    var trace = TraceLine(statements, 0);
                    return Traced(trace, statements.First.Transform(this));
                } else {
                    var trace = TraceLine(statements, 0);
                    return Traced(trace, statements.First.TransformResult(this, resultOperation));
                }

            } else {
                var result = new AstBlock();

                if (prologue != null) {
                    result.Add(prologue);
                }

                // transform all but the last statement if it is an expression stmt:
                int last = statements.Count - 1;
                for (int i = 0; i < last; i++) {
                    var trace = TraceLine(statements, i);
                    result.Add(Traced(trace, statements[i].Transform(this)));
                }

                if (statements.Count > 0) {
                    var trace = TraceLine(statements, last);
                    if (resultOperation.IsIgnore) {
                        result.Add(Traced(trace, statements.Last.Transform(this)));
                    } else {
                        result.Add(Traced(trace, statements.Last.TransformResult(this, resultOperation)));
                    }
                }

                if (epilogue != null) {
                    result.Add(epilogue);
                }

                result.Add(AstUtils.Empty());
                return result;
            }
        }

        internal MSA.Expression/*!*/ TransformStatementsToBooleanExpression(Statements statements, bool positive) {
            return TransformStatementsToExpression(statements, true, positive);
        }

        internal MSA.Expression/*!*/ TransformStatementsToExpression(Statements statements) {
            return TransformStatementsToExpression(statements, false, false);
        }

        private MSA.Expression/*!*/ TransformStatementsToExpression(Statements statements, bool toBoolean, bool positive) {
            // (see TransformStatements)
            if (!StackGuard.IsSufficient) {
                return StackGuard.RunOnFreshStack(() => TransformStatementsToExpression(statements, toBoolean, positive));
            }

            if (statements == null || statements.Count == 0) {
                return toBoolean ? AstUtils.Constant(!positive) : AstUtils.Constant(null);
            }

            int lastIndex = statements.Count - 1;
            if (statements.Count == 1) {
                var trace = TraceLine(statements, 0);
                return Traced(trace, toBoolean ? statements.Last.TransformCondition(this, positive) : statements.Last.TransformReadStep(this));
            }

            // the last statement is transformed first (as it always has been), but its hook is
            // claimed after the others', in source order
            var last = toBoolean ? statements.Last.TransformCondition(this, positive) : statements.Last.TransformReadStep(this);

            var result = new AstBlock();
            for (int i = 0; i < lastIndex; i++) {
                var trace = TraceLine(statements, i);
                result.Add(Traced(trace, statements[i].Transform(this)));
            }
            result.Add(Traced(TraceLine(statements, lastIndex), last));

            return result;
        }

        /// <summary>
        /// The TracePoint :line hook for a statement if it is the first statement on its line (MRI
        /// reports a line once however many statements it holds), or null. Claimed before the
        /// statement is transformed, so that statements nested in it come second.
        /// </summary>
        private MSA.Expression TraceLine(Statements/*!*/ statements, int index) {
            if (IsTransparentStatement(statements[index])) {
                return null;
            }
            return TraceLine(statements.GetStartLine(index));
        }

        /// <summary>
        /// The :line hook for a line, or null if the line has had its hook in the current scope.
        /// </summary>
        internal MSA.Expression TraceLine(int line) {
            var scope = _currentVariableScope;
            if (!_traceable || scope == null || line <= 0 || line == scope.LastTracedLine) {
                return null;
            }
            scope.LastTracedLine = line;

            var coverage = Coverage;
            if (coverage != null && coverage.IsCounted(line)) {
                // normally marked already (CoverageState.LineCollector); this catches what it misses
                coverage.MarkLine(line);
            } else {
                coverage = null;
            }
            return new TraceLineExpression(scope.RuntimeScopeVariable, SourcePath, line, coverage);
        }

        internal void SetLastTracedLine(int line) {
            if (_currentVariableScope != null) {
                _currentVariableScope.LastTracedLine = line;
            }
        }

        /// <summary>
        /// A begin/end block around statements has no :line event of its own (as in MRI); the
        /// first statement in it has.
        /// </summary>
        internal static bool IsTransparentStatement(Expression/*!*/ statement) {
            var body = statement as Body;
            return body != null && body.Statements.Count > 0;
        }

        /// <summary>
        /// Where the :line hooks count for the Coverage library, if the code is measured.
        /// </summary>
        internal LineCoverage Coverage { get; set; }

        /// <summary>
        /// The call counter of the method body being compiled, if :methods coverage measures it.
        /// </summary>
        internal MethodCoverage MethodCoverage { get; set; }

        private static MSA.Expression/*!*/ Traced(MSA.Expression trace, MSA.Expression/*!*/ transformed) {
            return trace != null ? Ast.Block(trace, transformed) : transformed;
        }

        internal AstExpressions/*!*/ TransformMapletsToExpressions(IList<Maplet>/*!*/ maplets) {
            Assert.NotNullItems(maplets);
            return TransformMapletsToExpressions(maplets, new AstExpressions(maplets.Count * 2));
        }

        internal AstExpressions/*!*/ TransformMapletsToExpressions(IList<Maplet>/*!*/ maplets, AstExpressions/*!*/ result) {
            Assert.NotNullItems(maplets);
            Assert.NotNull(result);

            foreach (Maplet entry in maplets) {
                result.Add(entry.Key.TransformRead(this));
                result.Add(entry.Value.TransformRead(this));
            }

            return result;
        }

        public MSA.Expression/*!*/ TransformToHashConstructor(IList<Maplet>/*!*/ maplets) {
            return MakeHashOpCall(TransformMapletsToExpressions(maplets));
        }

        internal MSA.Expression/*!*/ MakeHashOpCall(IEnumerable<MSA.Expression>/*!*/ expressions) {
            return Methods.MakeHash.OpCall(CurrentScopeVariable, AstUtils.NewArrayHelper(typeof(object), expressions));
        }

        internal MSA.Expression/*!*/ MakeKeywordArgumentsHashOpCall(IEnumerable<MSA.Expression>/*!*/ expressions) {
            return Methods.MakeKeywordArgumentsHash.OpCall(CurrentScopeVariable, AstUtils.NewArrayHelper(typeof(object), expressions));
        }

        internal static bool CanAssign(Type/*!*/ to, Type/*!*/ from) {
            return to.IsAssignableFrom(from) && (to.IsValueType == from.IsValueType);
        }

        internal MSA.Expression/*!*/ AddReturnTarget(MSA.Expression/*!*/ expression) {
            return CurrentFrame.AddReturnTarget(expression);
        }

        internal MSA.LabelTarget/*!*/ ReturnLabel {
            get { return CurrentFrame.ReturnLabel; }
        }

        /// <summary>
        /// The class or module body a jump to the current loop - or, if there is none, the current
        /// block or method frame - has to escape first, or null if the target is inside the body.
        /// </summary>
        internal ModuleScope GetModuleBodyToEscape(bool toLoop) {
            var module = GetEnclosingModuleFrame();
            if (module == null) {
                return null;
            }
            if (toLoop) {
                return CurrentLoop == module.OuterLoop ? module : null;
            }
            return CurrentBlock == module.OuterBlock ? module : null;
        }

        internal MSA.Expression/*!*/ Return(MSA.Expression/*!*/ expression) {
            var module = GetModuleBodyToEscape(false);
            if (module != null) {
                return module.Escape(expression, (gen, value) => gen.Return(value));
            }

            MSA.LabelTarget returnLabel = ReturnLabel;
            if (returnLabel.Type != typeof(void) && expression.Type == typeof(void)) {
                expression = Ast.Block(expression, AstUtils.Constant(null, typeof(object)));
            } else if (returnLabel.Type != expression.Type) {
                if (!CanAssign(returnLabel.Type, expression.Type)) {
                    // Add conversion step to the AST
                    expression = Ast.Convert(expression, returnLabel.Type);
                }
            }
            return Ast.Return(returnLabel, expression);
        }

        internal MSA.Expression ClearDebugInfo() {
            return _sequencePointClearance;
        }

        internal MSA.Expression/*!*/ AddDebugInfo(MSA.Expression/*!*/ expression, SourceSpan location) {
            if (_document != null) {
                // TODO: should we add clearance for non-goto expressions?
                // return AstUtils.AddDebugInfo(expression, _document, location.Start, location.End);
                var sequencePoint = Ast.DebugInfo(_document, location.Start.Line, location.Start.Column, location.End.Line, location.End.Column);
                return Ast.Block(sequencePoint, expression);
            } else {
                return expression;
            }
        }

        internal MSA.Expression DebugMarker(string/*!*/ marker) {
            return _debugCompiler ? Methods.X.OpCall(AstUtils.Constant(marker)) : null;
        }

        internal MSA.Expression/*!*/ DebugMark(MSA.Expression/*!*/ expression, string/*!*/ marker) {
            return _debugCompiler ? Ast.Block(Methods.X.OpCall(AstUtils.Constant(marker)), expression) : expression;
        }

        internal MSA.Expression/*!*/ TryCatchAny(MSA.Expression/*!*/ tryBody, MSA.Expression/*!*/ catchBody) {
            var variable = CurrentScope.DefineHiddenVariable("#value", tryBody.Type);
            var exception = Ast.Parameter(typeof(Exception), "#exception");

            return
                Ast.Block(
                    Ast.TryCatch(
                        Ast.Assign(variable, tryBody),
                        // an exception makes defined? nil, but throw/break/return still unwind through it
                        Ast.Catch(exception, Ast.Block(
                            Ast.IfThen(Ast.TypeIs(exception, typeof(StackUnwinder)), Ast.Rethrow()),
                            Ast.Assign(variable, catchBody)
                        ))
                    ),
                    variable
                );
        }
    }
}

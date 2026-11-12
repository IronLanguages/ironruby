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
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    
    public partial class SourceUnitTree : Node {

        private readonly LexicalScope/*!*/ _definedScope;
        private readonly Statements/*!*/ _statements;
        private readonly RubyEncoding/*!*/ _encoding;

        // An offset of the first byte after __END__ that can be read via DATA constant or -1 if __END__ is not present.
        private readonly int _dataOffset;

        public Statements/*!*/ Statements {
            get { return _statements; }
        }

        public RubyEncoding/*!*/ Encoding {
            get { return _encoding; }
        }

        public SourceUnitTree(LexicalScope/*!*/ definedScope, Statements/*!*/ statements, RubyEncoding/*!*/ encoding, int dataOffset)
            : base(SourceSpan.None) {
            Assert.NotNull(definedScope, statements, encoding);

            _definedScope = definedScope;
            _statements = statements;
            _encoding = encoding;
            _dataOffset = dataOffset;
        }

        // The locals an eval's string declares at its top level, which live in the scope it runs in
        // rather than in storage of its own. Those the scope already has belong to the outer lexical scope.
        // A flip-flop's state is one of them too: left undeclared, a flip-flop inside a block would
        // create it afresh in the block's scope on every iteration and never stay on.
        private string/*!*/[]/*!*/ GetEvalDeclaredVariables() {
            var result = new List<string>();
            foreach (var entry in _definedScope) {
                string name = entry.Key;
                // A flip-flop's hidden state too: a block in the eval would otherwise define it in its
                // own scope on every call and lose the state between iterations.
                if (entry.Value.DefinitionLexicalDepth < 0 && name.Length > 0 &&
                    (name[0] == '_' || Char.IsLetter(name[0]) || name.StartsWith(RangeExpression.FlipFlopVariablePrefix, StringComparison.Ordinal))) {
                    result.Add(name);
                }
            }
            return result.ToArray();
        }

        private ScopeBuilder/*!*/ DefineLocals() {
            return new ScopeBuilder(_definedScope.AllocateClosureSlotsForLocals(0), null, _definedScope);
        }

        internal MSA.Expression<T>/*!*/ Transform<T>(AstGenerator/*!*/ gen) {
            Debug.Assert(gen != null);

            ScopeBuilder scope = DefineLocals();

            MSA.ParameterExpression[] parameters;
            MSA.ParameterExpression selfVariable;
            MSA.ParameterExpression runtimeScopeVariable;
            MSA.ParameterExpression blockParameter;

            if (gen.CompilerOptions.FactoryKind == TopScopeFactoryKind.None ||
                gen.CompilerOptions.FactoryKind == TopScopeFactoryKind.ModuleEval) {
                parameters = new MSA.ParameterExpression[4];

                runtimeScopeVariable = parameters[0] = Ast.Parameter(typeof(RubyScope), "#scope");
                selfVariable = parameters[1] = Ast.Parameter(typeof(object), "#self");
                parameters[2] = Ast.Parameter(typeof(RubyModule), "#module");
                blockParameter = parameters[3] = Ast.Parameter(typeof(Proc), "#block");
            } else {
                parameters = new MSA.ParameterExpression[2];

                runtimeScopeVariable = parameters[0] = Ast.Parameter(typeof(RubyScope), "#scope");
                selfVariable = parameters[1] = Ast.Parameter(typeof(object), "#self");

                blockParameter = null;
            }

            gen.EnterSourceUnit(
                scope,
                selfVariable,
                runtimeScopeVariable,
                blockParameter,
                gen.CompilerOptions.TopLevelMethodName, // method name for blocks
                null                                    // parameters for super calls
            );
            gen.CurrentMethod.HasOwnCompiledFrame = true;

            MSA.Expression body;

            if (_statements.Count > 0) {
                if (gen.PrintInteractiveResult) {
                    var resultVariable = scope.DefineHiddenVariable("#result", typeof(object));

                    var epilogue = Methods.PrintInteractiveResult.OpCall(runtimeScopeVariable,
                        AstUtils.LightDynamic(ConvertToSAction.Make(gen.Context), typeof(MutableString),
                            CallSiteBuilder.InvokeMethod(gen.Context, "inspect", RubyCallSignature.WithScope(0),
                                gen.CurrentScopeVariable, resultVariable
                            )
                        )
                    );

                    body = gen.TransformStatements(null, _statements, epilogue, ResultOperation.Store(resultVariable));
                } else {
                    body = gen.TransformStatements(_statements, ResultOperation.Return);
                }

                // TODO:
                var exceptionVariable = Ast.Parameter(typeof(Exception), "#exception");
                var kind = gen.CompilerOptions.FactoryKind;
                if (kind == TopScopeFactoryKind.None || kind == TopScopeFactoryKind.ModuleEval) {
                    body = AstUtils.Try(
                        body
                    ).Filter(exceptionVariable, Methods.TraceTopLevelCodeFrame.OpCall(runtimeScopeVariable, exceptionVariable),
                        Ast.Empty()
                    );
                } else {
                    // a file's top level: `return` from a block in it ends the file
                    body = AstUtils.Try(
                        body
                    ).Filter(exceptionVariable, Methods.IsTopLevelReturn.OpCall(runtimeScopeVariable, exceptionVariable),
                        Ast.Empty()
                    ).Finally(
                        Methods.LeaveMethodFrame.OpCall(runtimeScopeVariable)
                    );
                }
            } else {
                body = AstUtils.Constant(null);
            }

            // scope initialization:
            MSA.Expression prologue;
            switch (gen.CompilerOptions.FactoryKind) {
                case TopScopeFactoryKind.None:
                    var declared = GetEvalDeclaredVariables();
                    prologue = declared.Length == 0 ?
                        Methods.InitializeScopeNoLocals.OpCall(runtimeScopeVariable, EnterInterpretedFrameExpression.Instance) :
                        Methods.InitializeEvalScope.OpCall(runtimeScopeVariable, AstUtils.Constant(declared), EnterInterpretedFrameExpression.Instance);
                    break;

                case TopScopeFactoryKind.ModuleEval:
                    prologue = Methods.InitializeScopeNoLocals.OpCall(runtimeScopeVariable, EnterInterpretedFrameExpression.Instance);
                    break;

                case TopScopeFactoryKind.Hosted:
                case TopScopeFactoryKind.File:
                case TopScopeFactoryKind.WrappedFile:
                    prologue = Methods.InitializeScope.OpCall(
                        runtimeScopeVariable, scope.MakeLocalsStorage(), scope.GetVariableNamesExpression(),
                        EnterInterpretedFrameExpression.Instance
                    );
                    break;

                case TopScopeFactoryKind.Main:
                    prologue = Methods.InitializeScope.OpCall(
                        runtimeScopeVariable, scope.MakeLocalsStorage(), scope.GetVariableNamesExpression(),
                        EnterInterpretedFrameExpression.Instance
                    );
                    if (_dataOffset >= 0) {
                        prologue = Ast.Block(
                            prologue,
                            Methods.SetDataConstant.OpCall(
                                runtimeScopeVariable,
                                gen.SourcePathConstant,
                                AstUtils.Constant(_dataOffset)
                            )
                        );
                    }
                    break;

                default:
                    throw Assert.Unreachable;
            }

            body = Ast.Block(prologue, body);

            body = gen.AddReturnTarget(scope.CreateScope(body));

            gen.LeaveSourceUnit();

            string encodedName = GetEncodedName(gen);
            return Ast.Lambda<T>(new CompiledFrameExpression(body, encodedName, 0), encodedName, parameters);
        }

        private static string/*!*/ GetEncodedName(AstGenerator/*!*/ gen) {
            return RubyStackTraceBuilder.EncodeMethodName(gen.TopLevelFrameLabel, gen.SourcePath, SourceSpan.None, gen.DebugMode);
        }
    }
}

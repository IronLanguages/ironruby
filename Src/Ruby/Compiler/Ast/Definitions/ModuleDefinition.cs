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
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstBlock = Microsoft.Scripting.Ast.BlockBuilder;
    
    public partial class ModuleDefinition : DefinitionExpression {
        /// <summary>
        /// Singleton classes don't have a name.
        /// </summary>
        private readonly ConstantVariable _qualifiedName;
        
        public ConstantVariable QualifiedName {
            get { return _qualifiedName; }
        }

        protected virtual bool IsSingletonDeclaration {
            get { return false; }
        }

        public ModuleDefinition(LexicalScope/*!*/ definedScope, ConstantVariable/*!*/ qualifiedName, Body/*!*/ body, SourceSpan location)
            : base(definedScope, body, location) {
            ContractUtils.RequiresNotNull(qualifiedName, "qualifiedName");

            _qualifiedName = qualifiedName;
        }

        protected ModuleDefinition(LexicalScope/*!*/ definedScope, Body/*!*/ body, SourceSpan location)
            : base(definedScope, body, location) {
            _qualifiedName = null;
        }
        
        internal virtual MSA.Expression/*!*/ MakeDefinitionExpression(AstGenerator/*!*/ gen) {
            MSA.Expression transformedQualifier;
            MSA.Expression name = QualifiedName.TransformName(gen);

            // Module#const_source_location reports the line of the `module` keyword:
            MSA.Expression sourcePath = gen.SourcePathConstant;
            MSA.Expression sourceLine = AstUtils.Constant(Location.Start.Line);

            switch (QualifiedName.TransformQualifier(gen, out transformedQualifier)) {
                case StaticScopeKind.Global:
                    return Methods.DefineGlobalModule.OpCall(gen.CurrentScopeVariable, name, sourcePath, sourceLine);

                case StaticScopeKind.EnclosingModule:
                    return Methods.DefineNestedModule.OpCall(gen.CurrentScopeVariable, name, sourcePath, sourceLine);

                case StaticScopeKind.Explicit:
                    return Methods.DefineModule.OpCall(gen.CurrentScopeVariable, AstUtils.Box(transformedQualifier), name, sourcePath, sourceLine);
            }

            throw Assert.Unreachable;
        }

        // MRI's label for the body's frame: "<class:B>" for `class A::B`, "<module:M>", "singleton class".
        private string/*!*/ FrameLabel {
            get {
                if (IsSingletonDeclaration) {
                    return "singleton class";
                }
                return ((this is ClassDefinition) ? "<class:" : "<module:") + QualifiedName.Name + ">";
            }
        }

        private ScopeBuilder/*!*/ DefineLocals() {
            return new ScopeBuilder(DefinedScope.AllocateClosureSlotsForLocals(0), null, DefinedScope);
        }

        internal sealed override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            string debugString = (IsSingletonDeclaration) ? "SINGLETON" : ((this is ClassDefinition) ? "CLASS" : "MODULE") + " " + QualifiedName.Name;

            ScopeBuilder outerLocals = gen.CurrentScope;
                
            // definition needs to take place outside the defined lexical scope:
            var definition = MakeDefinitionExpression(gen);
            var selfVariable = outerLocals.DefineHiddenVariable("#module", typeof(RubyModule));
            var parentScope = gen.CurrentScopeVariable;

            // The body is a frame of its own, as in MRI: a backtrace from inside it reads
            // "in '<class:C>'" rather than naming whatever frame the definition is written in.
            // So it is compiled as a lambda of its own, named for the backtrace, and called in place.
            string frameLabel = FrameLabel;
            var parentParameter = Ast.Parameter(typeof(RubyScope), "#parent");
            var selfParameter = Ast.Parameter(typeof(RubyModule), "#module");

            // inner locals:
            ScopeBuilder scope = DefineLocals();
            var scopeVariable = scope.DefineHiddenVariable("#scope", typeof(RubyScope));
            
            gen.EnterModuleDefinition(
                scope,
                selfParameter, 
                scopeVariable, 
                IsSingletonDeclaration,
                frameLabel
            );
            gen.GetEnclosingModuleFrame().OuterLocals = outerLocals;

            // transform body:
            MSA.Expression transformedBody = Body.TransformRead(gen);

            // outer local:
            MSA.Expression resultVariable = outerLocals.DefineHiddenVariable("#result", typeof(object));
            var bodyResult = Ast.Variable(typeof(object), "#result");
            var moduleFrame = gen.GetEnclosingModuleFrame();

            var bodyLambda = Ast.Lambda<Func<RubyScope, RubyModule, object>>(
                Ast.Block(new[] { bodyResult },
                    scope.CreateScope(
                        scopeVariable,
                        Methods.CreateModuleScope.OpCall(
                            scope.MakeLocalsStorage(),
                            scope.GetVariableNamesExpression(), 
                            parentParameter, 
                            selfParameter
                        ),
                        gen.Traceable ? Ast.Block(
                            // TracePoint :class and :end (class bodies are rare, a plain call is cheap enough)
                            Methods.TraceClassEvent.OpCall(scopeVariable, gen.SourcePathConstant, AstUtils.Constant(Location.Start.Line)),
                            Ast.Assign(bodyResult, new TraceReturnExpression(
                                scopeVariable, moduleFrame.AddReturnTarget(AstUtils.Box(transformedBody)), TraceEvents.End, gen.SourcePath, Location.End.Line
                            )),
                            AstUtils.Empty()
                        ) : Ast.Block(
                            Ast.Assign(bodyResult, moduleFrame.AddReturnTarget(AstUtils.Box(transformedBody))),
                            AstUtils.Empty()
                        )
                    ),
                    bodyResult
                ),
                RubyStackTraceBuilder.EncodeMethodName(frameLabel, gen.SourcePath, Location, gen.DebugMode),
                new[] { parentParameter, selfParameter }
            );
            
            // begin with new scope
            //   self = DefineModule/Class(... parent scope here ...)
            //   <body>
            // end
            gen.LeaveModuleDefinition();

            MSA.Expression result = new AstBlock {
                gen.DebugMarker(debugString),
                Ast.Assign(selfVariable, definition),
                moduleFrame.ResetEscapes(),
                Ast.Assign(resultVariable, Ast.Invoke(bodyLambda, parentScope, selfVariable)),
                moduleFrame.MakeEscapedJumps(gen),
                gen.DebugMarker("END OF " + debugString),
                resultVariable
            };

            return result;
        }
    }
}

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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstExpressions = ReadOnlyCollectionBuilder<MSA.Expression>;
    using AstParameters = ReadOnlyCollectionBuilder<MSA.ParameterExpression>;
    
    public partial class MethodDefinition : DefinitionExpression {
        // self, block
        internal const int HiddenParameterCount = 2;
            
        /// <summary>
        /// Non-null for singleton/class methods.
        /// </summary>
        private readonly Expression _target;

        private readonly string/*!*/ _name;
        private readonly Parameters/*!*/ _parameters;

        public Expression Target {
            get { return _target; }
        }

        public string/*!*/ Name { 
            get { return _name; }
        }

        public Parameters/*!*/ Parameters {
            get { return _parameters; }
        }

        /// <summary>
        /// MRI's use_block: the method declares a block parameter, yields, or calls super passing
        /// its block on. A block given to a method without it may be ignored, which MRI warns about.
        /// </summary>
        // The prism bridge works this out; a method built any other way is assumed to use its
        // block, so it is never warned about or given the block-less fast path.
        public bool UsesBlock { get; set; } = true;

        public MethodDefinition(LexicalScope/*!*/ definedScope, Expression target, string/*!*/ name, Parameters parameters, Body/*!*/ body, 
            SourceSpan location)
            : base(definedScope, body, location) {
            Assert.NotNull(name);

            // only for-loop block might use other than local variable for unsplat:
            Debug.Assert(parameters.Unsplat == null || parameters.Unsplat is LocalVariable);
                
            _target = target;
            _name = name;
            _parameters = parameters ?? Parameters.Empty;
        }

        /// <summary>
        /// A method whose body the front end builds when it is first asked for - normally when the method
        /// is first called. Building it adds the body's locals to <paramref name="definedScope"/>.
        /// </summary>
        public MethodDefinition(LexicalScope/*!*/ definedScope, Expression target, string/*!*/ name, Parameters parameters,
            Func<Body>/*!*/ bodyFactory, SourceSpan location)
            : base(definedScope, bodyFactory, location) {
            Assert.NotNull(name);
            Debug.Assert(parameters == null || parameters.Unsplat == null || parameters.Unsplat is LocalVariable);

            _target = target;
            _name = name;
            _parameters = parameters ?? Parameters.Empty;
        }

        private ScopeBuilder/*!*/ DefineLocals(out AstParameters/*!*/ parameters) {
            // Method signature:
            // <self>, &<block>, <leading-mandatory>, <trailing-mandatory>, <optional>, *<unsplat>
            
            parameters = new AstParameters(
                HiddenParameterCount +
                _parameters.Mandatory.Length +
                _parameters.Optional.Length +
                (_parameters.Unsplat != null ? 1 : 0)
            );

            int closureIndex = 0;
            int firstClosureParam = 1;
            parameters.Add(Ast.Parameter(typeof(object), "#self"));

            if (_parameters.Block != null) {
                parameters.Add(Ast.Parameter(typeof(Proc), _parameters.Block.Name));
                _parameters.Block.SetClosureIndex(closureIndex++);
            } else {
                parameters.Add(Ast.Parameter(typeof(Proc), "#block"));
                firstClosureParam++;
            }

            foreach (LeftValue lvalue in _parameters.Mandatory) {
                var param = lvalue as LocalVariable;
                if (param != null) {
                    parameters.Add(Ast.Parameter(typeof(object), param.Name));
                    param.SetClosureIndex(closureIndex++);
                } else {
                    // TODO:
                    throw new NotSupportedException("TODO: compound parameters");
                }
            }

            foreach (var lvalue in _parameters.Optional) {
                var param = (LocalVariable)lvalue.Left;
                parameters.Add(Ast.Parameter(typeof(object), param.Name));
                param.SetClosureIndex(closureIndex++);
            }

            if (_parameters.Unsplat != null) {
                var unsplatLocal = (LocalVariable)_parameters.Unsplat;
                parameters.Add(Ast.Parameter(typeof(object), unsplatLocal.Name));
                unsplatLocal.SetClosureIndex(closureIndex++);
            }

            // allocate closure slots for locals:
            int localCount = DefinedScope.AllocateClosureSlotsForLocals(closureIndex);

            return new ScopeBuilder(parameters, firstClosureParam, localCount, null, DefinedScope);
        }

        /// <summary>
        /// The label MRI 3.4 and later puts on a method frame: "M::C#foo" for an instance method,
        /// "M::C.foo" for a method on a module's singleton, and the bare name where there is no
        /// module to name - a singleton method of an ordinary object (including main), or a method
        /// defined in an anonymous module.
        ///
        /// The owner is only known once the method is defined, which is also when the body is
        /// compiled, so the label can be baked into the frame name along with everything else.
        /// </summary>
        internal static string/*!*/ QualifyFrameLabel(string/*!*/ name, RubyModule declaringModule) {
            if (declaringModule == null) {
                return name;
            }

            RubyClass cls = declaringModule as RubyClass;
            if (cls != null && cls.IsSingletonClass) {
                RubyModule attached = cls.SingletonClassOf as RubyModule;
                return (attached != null && attached.Name != null) ? attached.Name + "." + name : name;
            }

            return (declaringModule.Name != null) ? declaringModule.Name + "#" + name : name;
        }

        internal MSA.LambdaExpression/*!*/ TransformBody(AstGenerator/*!*/ gen, RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule,
            StrongBox<RefinementActivation> definitionRefinements) {
            return TransformBody(gen, ExactConstant(declaringScope, typeof(RubyScope)), ExactConstant(declaringModule, typeof(RubyModule)),
                QualifyFrameLabel(_name, declaringModule), definitionRefinements);
        }

        /// <summary>
        /// A compiled lambda keeps its object constants in an object[] and casts each one back on
        /// every load. Typed as their base class the declaring scope (a top-level, module or method
        /// scope) and module (a class, mostly) miss the inline exact-type check and go to the
        /// runtime's class-cast helper, twice per call of the method; typed as what they are, the
        /// cast is one compare, and the up-cast to the parameter type is free.
        /// </summary>
        private static MSA.Expression/*!*/ ExactConstant(object/*!*/ value, Type/*!*/ type) {
            return AstUtils.Convert(Ast.Constant(value, value.GetType()), type);
        }

        /// <summary>
        /// The body with the declaring scope and module given as expressions rather than baked-in
        /// objects. Ahead-of-time compilation (Util/aot) uses it: there, both are only known when
        /// the `def' runs, so they are parameters of a factory that builds the body's delegate.
        /// </summary>
        internal MSA.LambdaExpression/*!*/ TransformBody(AstGenerator/*!*/ gen, MSA.Expression/*!*/ declaringScope, MSA.Expression/*!*/ declaringModule,
            string/*!*/ frameLabel, StrongBox<RefinementActivation> definitionRefinements) {
            string encodedName = RubyStackTraceBuilder.EncodeMethodName(frameLabel, gen.SourcePath, Location, gen.DebugMode);

            // a body built on demand adds its locals to the scope: build it before they are allocated
            Body methodBody = Body;

            AstParameters parameters;
            ScopeBuilder scope = DefineLocals(out parameters);

            var scopeVariable = scope.DefineHiddenVariable("#scope", typeof(RubyMethodScope));
            var selfParameter = parameters[0];
            var blockParameter = parameters[1];

            // exclude block parameter even if it is explicitly specified:
            int visiblePrameterCountAndSignatureFlags = (parameters.Count - 2) << 2;
            if (_parameters.Block != null) {
                visiblePrameterCountAndSignatureFlags |= RubyMethodScope.HasBlockFlag;
            }
            if (_parameters.Unsplat != null) {
                visiblePrameterCountAndSignatureFlags |= RubyMethodScope.HasUnsplatFlag;
            }

            gen.EnterMethodDefinition(
                scope,
                selfParameter,
                scopeVariable,
                blockParameter,
                _name,
                _parameters
            );

            // Blocks written inside this method are labelled "block in <this frame's label>",
            // so they need the qualified form rather than the bare method name.
            gen.CurrentMethod.FrameLabel = frameLabel;

            // profiling:
            MSA.Expression profileStart = AstUtils.Empty();
            MSA.Expression profileEnd = AstUtils.Empty();

            if (gen.Profiler != null) {
                int profileTickIndex = gen.Profiler.GetTickIndex(encodedName);
                var stampVariable = scope.DefineHiddenVariable("#stamp", typeof(long));
                profileStart = Ast.Assign(stampVariable, Methods.Stopwatch_GetTimestamp.OpCall());
                profileEnd = Methods.UpdateProfileTicks.OpCall(AstUtils.Constant(profileTickIndex), stampVariable);
            }

            // tracing:
            MSA.Expression traceCall, traceReturn;
            if (gen.TraceEnabled) {
                traceCall = Methods.TraceMethodCall.OpCall(
                    scopeVariable, 
                    gen.SourcePathConstant, 
                    AstUtils.Constant(Location.Start.Line)
                );

                traceReturn = Methods.TraceMethodReturn.OpCall(
                    gen.CurrentScopeVariable,
                    gen.SourcePathConstant,
                    AstUtils.Constant(Location.End.Line)
                );
            } else {
                traceCall = traceReturn = AstUtils.Empty();
            }

            MSA.ParameterExpression unwinder;
            
            // see RubyScope.GetActiveRefinements:
            MSA.Expression recordRefinements = (definitionRefinements != null)
                ? (MSA.Expression)Ast.Assign(
                    Ast.Property(scopeVariable, typeof(RubyMethodScope).GetProperty("DefinitionRefinements")),
                    Ast.Constant(definitionRefinements, typeof(StrongBox<RefinementActivation>)))
                : AstUtils.Empty();

            // :methods coverage: one call, counted where the frame is entered
            MSA.Expression countCall;
            var methodCoverage = gen.MethodCoverage;
            if (methodCoverage != null) {
                countCall = Ast.Call(AstUtils.Constant(methodCoverage), typeof(MethodCoverage).GetMethod("Count"));
            } else {
                countCall = AstUtils.Empty();
            }

            MSA.Expression body = AstUtils.Try(
                countCall,
                recordRefinements,
                profileStart,
                _parameters.TransformOptionalsInitialization(gen),
                traceCall,
                methodBody.TransformResult(gen, ResultOperation.Return)
            ).Filter(unwinder = Ast.Parameter(typeof(Exception), "#u"), Methods.IsMethodUnwinderTargetFrame.OpCall(scopeVariable, unwinder),
                Ast.Return(gen.ReturnLabel, Methods.GetMethodUnwinderReturnValue.OpCall(unwinder))
            ).Finally(  
                // leave frame:
                Methods.LeaveMethodFrame.OpCall(scopeVariable),
                Ast.Empty(),
                profileEnd,
                traceReturn
            );

            // TracePoint :return: falling off the end reports the line of `end'; an explicit
            // `return' reports its own line and jumps past this hook (see ReturnStatement).
            if (gen.Traceable) {
                body = new TraceReturnExpression(scopeVariable, gen.AddReturnTarget(body), TraceEvents.Return, gen.SourcePath, Location.End.Line);
                if (gen.CurrentMethod.ExplicitReturnLabel != null) {
                    body = Ast.Label(gen.CurrentMethod.ExplicitReturnLabel, body);
                }
            } else {
                body = gen.AddReturnTarget(body);
            }

            body = scope.CreateScope(
                scopeVariable,
                Methods.CreateMethodScope.OpCall(new AstExpressions {
                    scope.MakeLocalsStorage(),
                    scope.GetVariableNamesExpression(),
                    Ast.Constant(visiblePrameterCountAndSignatureFlags),
                    declaringScope,
                    declaringModule,
                    Ast.Constant(_name),
                    selfParameter, blockParameter,
                    EnterInterpretedFrameExpression.Instance
                }),
                body
            );

            gen.LeaveMethodDefinition();

            return CreateLambda(encodedName, parameters, body);
        }

        private static MSA.LambdaExpression/*!*/ CreateLambda(string/*!*/ name, AstParameters/*!*/ parameters, MSA.Expression/*!*/ body) {
            var result = MethodDispatcher.CreateRubyMethodLambda(body, name, parameters);
            if (result != null) {
                return result;
            } 

            // to many parameters for Func<> delegate -> use object[]:
            MSA.ParameterExpression array = Ast.Parameter(typeof(object[]), "#params");
            var actualParameters = new AstParameters() { parameters[0], parameters[1], array };
            // Drop the two implicit parameters - self and the block - so that what is left is
            // exactly the declared parameters, in order, which is how they are read back out of
            // the object[].  Both live at the front, so both removals are at index 0: removing
            // index 1 second would take the *first declared* parameter instead of the block, and
            // leave the block's Proc-typed variable to be assigned an object out of the array.
            parameters.RemoveAt(0);
            parameters.RemoveAt(0);

            // ReadOnlyCollectionBuilder's int constructor takes a capacity, not a length, so the
            // collection starts empty - assigning through the indexer would be an index out of
            // range on a list of zero items.
            var bodyWithParamInit = new AstExpressions(parameters.Count + 1);
            for (int i = 0; i < parameters.Count; i++) {
                bodyWithParamInit.Add(Ast.Assign(parameters[i], Ast.ArrayIndex(array, AstUtils.Constant(i))));
            }
            bodyWithParamInit.Add(body);

            return Ast.Lambda<Func<object, Proc, object[], object>>(
                Ast.Block(
                    parameters, 
                    bodyWithParamInit
                ),
                name,
                actualParameters
            );
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            return Methods.DefineMethod.OpCall(
                (_target != null) ? AstUtils.Box(_target.TransformRead(gen)) : AstUtils.Constant(null),
                gen.CurrentScopeVariable,
                Ast.Constant(new RubyMethodBody(this, gen.Document, gen.Encoding, gen.Coverage))
            );
        }
    }
}

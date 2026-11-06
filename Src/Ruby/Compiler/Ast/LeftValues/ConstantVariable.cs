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
using System.Diagnostics;
using System.Reflection;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using IronRuby.Runtime;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using System.Collections.Generic;
    
    internal enum StaticScopeKind {
        Global,
        EnclosingModule,
        Explicit
    }

    public partial class ConstantVariable : Variable {
        private readonly bool _explicitlyBound;
        private readonly Expression _qualifier;

        public Expression Qualifier {
            get { return _qualifier; }
        }

        public bool IsGlobal {
            get {
                return _explicitlyBound && _qualifier == null;
            }
        }

        public bool IsBound {
            get {
                return _explicitlyBound;
            }
        }

        /// <summary>
        /// Unbound constant (Foo).
        /// </summary>
        public ConstantVariable(string/*!*/ name, SourceSpan location)
            : base(name, location) {

            _qualifier = null;
            _explicitlyBound = false;
        }

        /// <summary>
        /// Bound constant (::Foo - bound to Object, qualifier.Foo - bound to qualifier object).
        /// </summary>
        public ConstantVariable(Expression qualifier, string/*!*/ name, SourceSpan location)
            : base(name, location) {

            _qualifier = qualifier;
            _explicitlyBound = true;
        }

        internal StaticScopeKind TransformQualifier(AstGenerator/*!*/ gen, out MSA.Expression transformedQualifier) {
            if (_qualifier != null) {
                Debug.Assert(_explicitlyBound);

                // qualifier.Foo
                transformedQualifier = _qualifier.TransformRead(gen);
                return StaticScopeKind.Explicit;
            } else if (_explicitlyBound) {
                // ::Foo
                transformedQualifier = null;
                return StaticScopeKind.Global;
            } else {
                // bound to the enclosing module:
                transformedQualifier = null;
                return StaticScopeKind.EnclosingModule;
            }
        }

        internal override MSA.Expression/*!*/ TransformReadVariable(AstGenerator/*!*/ gen, bool tryRead) {
            if (!tryRead) {
                return TransformRead(gen, OpGet);
            }

            // `X ||= v` must not raise when X is undefined: it is defined?(X) ? X : (X = v)
            return Ast.Condition(
                TransformRead(gen, OpIsDefined),
                AstUtils.Box(TransformRead(gen, OpGet)),
                AstUtils.Constant(null, typeof(object))
            );
        }

        /// <summary>
        /// The qualifier of `expr::NAME`, so that an assignment evaluates it once for both the
        /// read and the write (`expr::NAME ||= v` must not run expr twice).
        /// </summary>
        internal override MSA.Expression TransformTargetRead(AstGenerator/*!*/ gen) {
            return (_qualifier != null && !(_qualifier is ConstantVariable))
                ? AstUtils.Box(_qualifier.TransformRead(gen)) : null;
        }

        internal override MSA.Expression/*!*/ TransformRead(AstGenerator/*!*/ gen, MSA.Expression targetValue, bool tryRead) {
            if (targetValue == null) {
                return TransformReadVariable(gen, tryRead);
            }

            var names = new[] { Name };
            if (!tryRead) {
                return Methods.GetExpressionQualifiedConstant.OpCall(targetValue, gen.CurrentScopeVariable,
                    Ast.Constant(new ExpressionQualifiedConstantSiteCache()), Ast.Constant(names));
            }

            // the target expression may assign a temporary, so evaluate it exactly once
            var qualifier = gen.CurrentScope.DefineHiddenVariable("#qualifier", typeof(object));
            return Ast.Condition(
                Methods.IsDefinedExpressionQualifiedConstant.OpCall(Ast.Assign(qualifier, targetValue), gen.CurrentScopeVariable,
                    Ast.Constant(new ExpressionQualifiedIsDefinedConstantSiteCache()), Ast.Constant(names)),
                AstUtils.Box(Methods.GetExpressionQualifiedConstant.OpCall(qualifier, gen.CurrentScopeVariable,
                    Ast.Constant(new ExpressionQualifiedConstantSiteCache()), Ast.Constant(names))),
                AstUtils.Constant(null, typeof(object))
            );
        }

        internal override MSA.Expression/*!*/ TransformWrite(AstGenerator/*!*/ gen, MSA.Expression targetValue, MSA.Expression/*!*/ rightValue) {
            if (targetValue == null) {
                return TransformWriteVariable(gen, rightValue);
            }

            // the qualifier is evaluated first: an op-assignment reads the constant out of it
            // while computing the value to write
            var qualifier = gen.CurrentScope.DefineHiddenVariable("#qualifier", typeof(object));
            return Ast.Block(
                Ast.Assign(qualifier, AstUtils.Box(targetValue)),
                Ast.Call(Methods.SetQualifiedConstant, AstUtils.Box(rightValue), qualifier, gen.CurrentScopeVariable,
                    TransformName(gen), gen.SourcePathConstant, AstUtils.Constant(Location.Start.Line), Ast.Constant(gen.Encoding))
            );
        }

        private const int OpGet = 0;
        private const int OpIsDefined = 1;

        private MSA.Expression/*!*/ TransformRead(AstGenerator/*!*/ gen, int opKind) {
            ConstantVariable constantQualifier = _qualifier as ConstantVariable;
            if (constantQualifier != null) {
                ConstantVariable constant;
                List<string> names = new List<string>();
                names.Add(Name);
                do {
                    names.Add(constantQualifier.Name);
                    constant = constantQualifier;
                    constantQualifier = constantQualifier.Qualifier as ConstantVariable;
                } while (constantQualifier != null);

                if (constant.Qualifier != null) {
                    // {expr}::A::B
                    return constant.MakeExpressionQualifiedRead(gen, opKind, names.ToReverseArray());
                } else {
                    // A::B
                    return MakeCachedRead(gen, opKind, constant.IsGlobal, true, Ast.Constant(names.ToReverseArray()));
                }
            } else if (_qualifier != null) {
                // {expr}::A
                return MakeExpressionQualifiedRead(gen, opKind, new[] { Name });
            } else {
                // A
                // ::A
                return MakeCachedRead(gen, opKind, IsGlobal, false, Ast.Constant(Name));
            }
        }

        private MSA.Expression/*!*/ MakeExpressionQualifiedRead(AstGenerator/*!*/ gen, int opKind, string/*!*/[]/*!*/ names) {
            Debug.Assert(_qualifier != null && !(_qualifier is ConstantVariable));

            object siteCache;
            MethodInfo op;

            if (opKind == OpIsDefined) {
                siteCache = new ExpressionQualifiedIsDefinedConstantSiteCache();
                op = Methods.IsDefinedExpressionQualifiedConstant;
            } else {
                siteCache = new ExpressionQualifiedConstantSiteCache();
                op = Methods.GetExpressionQualifiedConstant;
            }

            var result = op.OpCall(
                AstUtils.Box(_qualifier.TransformRead(gen)), 
                gen.CurrentScopeVariable, 
                Ast.Constant(siteCache), 
                Ast.Constant(names)
            );

            return opKind == OpIsDefined ? Ast.TryCatch(result, Ast.Catch(typeof(Exception), AstFactory.False)) : result;
        }

        // if (site.Version == <context>.ConstantAccessVersion) {
        //   object value = site.Value;
        //   if (value.GetType() == typeof(WeakReference)) {
        //     if (value == ConstantSiteCache.Missing) {
        //       <result> = ConstantMissing(...);
        //     } else {
        //       <result> = ((WeakReference)value).Target;
        //     }
        //   } else {
        //     <result> = value;
        //   }
        // } else {
        //   <result> = GetConstant(...);
        // }
        private static MSA.Expression/*!*/ MakeCachedRead(AstGenerator/*!*/ gen, int opKind, bool isGlobal, bool isQualified,
            MSA.Expression/*!*/ name) {

            object siteCache;
            MSA.ParameterExpression siteVar, valueVar;
            FieldInfo versionField, valueField;
            MSA.Expression readValue;
            MSA.Expression fallback;

            if (opKind == OpIsDefined) {
                siteCache = new IsDefinedConstantSiteCache();
                gen.CurrentScope.GetIsDefinedConstantSiteCacheVariables(out siteVar);
                versionField = Fields.IsDefinedConstantSiteCache_Version;
                valueField = Fields.IsDefinedConstantSiteCache_Value;

                readValue = Ast.Field(siteVar, valueField);

                fallback = (isQualified) ? 
                    Methods.IsDefinedQualifiedConstant.OpCall(gen.CurrentScopeVariable, siteVar, name, AstUtils.Constant(isGlobal)) :
                    (isGlobal ? Methods.IsDefinedGlobalConstant : Methods.IsDefinedUnqualifiedConstant).
                        OpCall(gen.CurrentScopeVariable, siteVar, name); 

            } else {
                siteCache = (ConstantSiteCache)new ConstantSiteCache();
                gen.CurrentScope.GetConstantSiteCacheVariables(out siteVar, out valueVar);
                versionField = Fields.ConstantSiteCache_Version;
                valueField = Fields.ConstantSiteCache_Value;

                MSA.Expression weakValue = Ast.Call(Ast.Convert(valueVar, typeof(WeakReference)), Methods.WeakReference_get_Target);
                if (!isQualified) {
                    weakValue = Ast.Condition(
                        // const missing:
                        Ast.Equal(valueVar, AstUtils.Constant(ConstantSiteCache.WeakMissingConstant)),
                        (isGlobal ? Methods.GetGlobalMissingConstant : Methods.GetMissingConstant).
                            OpCall(gen.CurrentScopeVariable, siteVar, name),

                        // weak value:
                        weakValue
                    );
                }

                readValue = Ast.Condition(
                    Ast.TypeEqual(Ast.Assign(valueVar, Ast.Field(siteVar, valueField)), typeof(WeakReference)),
                    weakValue,
                    valueVar
                );

                fallback = (isQualified ? Methods.GetQualifiedConstant : Methods.GetUnqualifiedConstant).
                    OpCall(gen.CurrentScopeVariable, siteVar, name, AstUtils.Constant(isGlobal));
            }

            return Ast.Block(
                Ast.Condition(
                    Ast.Equal(
                        Ast.Field(Ast.Assign(siteVar, Ast.Constant(siteCache)), versionField),
                        Ast.Field(Ast.Constant(gen.Context), Fields.RubyContext_ConstantAccessVersion)
                    ),
                    readValue,
                    fallback
                )
            ); 
        }

        internal override MSA.Expression/*!*/ TransformWriteVariable(AstGenerator/*!*/ gen, MSA.Expression/*!*/ rightValue) {
            MSA.Expression transformedName = TransformName(gen);
            MSA.Expression transformedQualifier;

            // Recorded for Module#const_source_location:
            MSA.Expression sourcePath = gen.SourcePathConstant;
            MSA.Expression sourceLine = AstUtils.Constant(Location.Start.Line);

            switch (TransformQualifier(gen, out transformedQualifier)) {
                case StaticScopeKind.Global:
                    return Methods.SetGlobalConstant.OpCall(AstUtils.Box(rightValue), gen.CurrentScopeVariable, transformedName, sourcePath, sourceLine, Ast.Constant(gen.Encoding));

                case StaticScopeKind.EnclosingModule:
                    return Methods.SetUnqualifiedConstant.OpCall(AstUtils.Box(rightValue), gen.CurrentScopeVariable, transformedName, sourcePath, sourceLine, Ast.Constant(gen.Encoding));

                case StaticScopeKind.Explicit:
                    // `qualifier::NAME = rhs` evaluates the qualifier first
                    var qualifier = gen.CurrentScope.DefineHiddenVariable("#qualifier", typeof(object));
                    return Ast.Block(
                        Ast.Assign(qualifier, AstUtils.Box(transformedQualifier)),
                        Ast.Call(Methods.SetQualifiedConstant, AstUtils.Box(rightValue), qualifier, gen.CurrentScopeVariable, transformedName, sourcePath, sourceLine, Ast.Constant(gen.Encoding))
                    );
            }

            throw Assert.Unreachable;
        }

        internal override MSA.Expression TransformDefinedCondition(AstGenerator/*!*/ gen) {
            return TransformRead(gen, OpIsDefined);
        }

        internal override string/*!*/ GetNodeName(AstGenerator/*!*/ gen) {
            return "constant";
        }
    }
}

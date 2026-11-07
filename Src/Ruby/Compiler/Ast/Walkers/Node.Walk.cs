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

namespace IronRuby.Compiler.Ast {
    // Walker dispatch:
    public partial class SourceUnitTree { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class BlockDefinition { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class BlockReference { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class Body { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class Maplet { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class Parameters { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class SplattedArgument { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class KeywordArgumentsTest { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class KeywordParameterRead { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class SplatParameterRead { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class KeywordSplatMerge { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }

    public partial class ClassDefinition { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ModuleDefinition { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class SingletonDefinition { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class MethodDefinition { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class LambdaDefinition { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }

    public partial class AndExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ArrayConstructor { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class IsDefinedExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class BlockExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class CaseExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ConditionalExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ForLoopExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class HashConstructor { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class IfExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class Literal { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class StringLiteral { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class SymbolLiteral { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class EncodingExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class MethodCall { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class MatchExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class NotExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class OrExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class RangeExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class RangeCondition { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class RegularExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class RegularExpressionCondition { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class RegexMatchReference { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class RescueExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class SelfReference { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class StringConstructor { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class SuperCall { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class UnlessExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class WhileLoopExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class YieldCall { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }

    public partial class ArrayItemAccess { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class AttributeAccess { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ClassVariable { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class CompoundLeftValue { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ConstantVariable { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class GlobalVariable { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class InstanceVariable { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class LocalVariable { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class Placeholder { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }

    public partial class MemberAssignmentExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ParallelAssignmentExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class SimpleAssignmentExpression { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }

    public partial class AliasStatement { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ShutdownHandlerStatement { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class UndefineStatement { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    
    public partial class BreakStatement { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class NextStatement { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class RedoStatement { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class RetryStatement { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ReturnStatement { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }

    public partial class RescueClause { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class WhenClause { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
    public partial class ElseIfClause { internal protected override void Walk(Walker/*!*/ walker) { walker.Walk(this); } }
}

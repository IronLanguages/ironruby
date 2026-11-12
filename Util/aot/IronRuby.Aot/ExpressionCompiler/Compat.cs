// The System.Core compiler reached into internal members of the expression node classes. On
// .NET 10 the nodes live in another assembly, so those are re-expressed over the public API
// (C# 14 extension members keep the ported call sites unchanged).
using System;
using System.Linq.Expressions;
using System.Reflection;

namespace IronRuby.Aot.Compiler {
    internal static class NodeCompat {
        extension(BlockExpression block) {
            public int ExpressionCount => block.Expressions.Count;
            public Expression GetExpression(int index) => block.Expressions[index];
        }

        extension(InvocationExpression node) {
            public LambdaExpression LambdaOperand =>
                node.Expression.NodeType == ExpressionType.Quote
                    ? (LambdaExpression)((UnaryExpression)node.Expression).Operand
                    : node.Expression as LambdaExpression;
        }

        extension(SwitchExpression node) {
            public bool IsLifted {
                get {
                    if (node.SwitchValue.Type.IsNullableType()) {
                        return node.Comparison == null ||
                            !TypeUtils.AreEquivalent(node.SwitchValue.Type, node.Comparison.GetParameters()[0].ParameterType.GetNonRefType());
                    }
                    return false;
                }
            }
        }

        extension(BinaryExpression node) {
            public bool IsLiftedLogical {
                get {
                    Type left = node.Left.Type, right = node.Right.Type;
                    MethodInfo method = node.Method;
                    ExpressionType kind = node.NodeType;
                    return (kind == ExpressionType.AndAlso || kind == ExpressionType.OrElse) &&
                        TypeUtils.AreEquivalent(right, left) && TypeUtils.IsNullableType(left) &&
                        method != null && TypeUtils.AreEquivalent(method.ReturnType, TypeUtils.GetNonNullableType(left));
                }
            }

            public Expression ReduceUserdefinedLifted() {
                var left = Expression.Parameter(node.Left.Type, "left");
                var right = Expression.Parameter(node.Right.Type, "right");
                string opName = node.NodeType == ExpressionType.AndAlso ? "op_False" : "op_True";
                MethodInfo opTrueFalse = TypeUtils.GetBooleanOperator(node.Method.DeclaringType, opName);
                return Expression.Block(new[] { left },
                    Expression.Assign(left, node.Left),
                    Expression.Condition(Expression.Property(left, "HasValue"),
                        Expression.Condition(Expression.Call(opTrueFalse, Expression.Call(left, "GetValueOrDefault", null)),
                            left,
                            Expression.Block(new[] { right },
                                Expression.Assign(right, node.Right),
                                Expression.Condition(Expression.Property(right, "HasValue"),
                                    Expression.Convert(Expression.Call(node.Method,
                                        Expression.Call(left, "GetValueOrDefault", null),
                                        Expression.Call(right, "GetValueOrDefault", null)), node.Type),
                                    Expression.Constant(null, node.Type)))),
                        Expression.Constant(null, node.Type)));
            }
        }

        extension(TypeBinaryExpression node) {
            public Expression ReduceTypeEqual() {
                Type cType = node.Expression.Type;
                Type typeOperand = node.TypeOperand;
                if (cType.IsValueType && !cType.IsNullableType()) {
                    return Expression.Block(node.Expression, Expression.Constant(cType == typeOperand.GetNonNullableType()));
                }
                if (node.Expression is ConstantExpression ce) {
                    return Expression.Constant(ce.Value != null && typeOperand.GetNonNullableType() == ce.Value.GetType());
                }
                if (cType.IsSealed && cType == typeOperand) {
                    return cType.IsNullableType()
                        ? Expression.NotEqual(node.Expression, Expression.Constant(null, cType))
                        : Expression.ReferenceNotEqual(node.Expression, Expression.Constant(null, cType));
                }
                if (node.Expression is ParameterExpression p && !p.IsByRef) {
                    return ByValParameterTypeEqual(p, typeOperand);
                }
                var parameter = Expression.Parameter(typeof(object));
                var expression = node.Expression;
                if (!TypeUtils.AreReferenceAssignable(typeof(object), expression.Type)) {
                    expression = Expression.Convert(expression, typeof(object));
                }
                return Expression.Block(new[] { parameter }, Expression.Assign(parameter, expression), ByValParameterTypeEqual(parameter, typeOperand));
            }
        }

        private static Expression ByValParameterTypeEqual(ParameterExpression value, Type typeOperand) {
            Expression getType = Expression.Call(value, typeof(object).GetMethod("GetType"));
            if (typeOperand.IsInterface) {
                var temp = Expression.Parameter(typeof(Type));
                getType = Expression.Block(new[] { temp }, Expression.Assign(temp, getType), temp);
            }
            return Expression.AndAlso(
                Expression.ReferenceNotEqual(value, Expression.Constant(null)),
                Expression.ReferenceEqual(getType, Expression.Constant(typeOperand.GetNonNullableType(), typeof(Type))));
        }
    }

    internal static class Strings {
        public const string InvalidArgumentValue = "Invalid argument value";
        public const string MethodPreconditionViolated = "Method precondition violated";
        public const string NonEmptyCollectionRequired = "Non-empty collection is required";
    }

    /// <summary>The compiler's error messages; the originals were generated resource strings.</summary>
    internal static class Error {
        private static Exception E(string what, params object[] args) =>
            new InvalidOperationException(what + (args.Length > 0 ? ": " + string.Join(", ", args) : ""));

        public static Exception AmbiguousJump(object a) => E("AmbiguousJump", a);
        public static Exception ArgumentMustBeBoolean() => new ArgumentException("Argument must be boolean");
        public static Exception ArgumentTypesMustMatch() => new ArgumentException("Argument types do not match");
        public static Exception ArrayTypeMustBeArray() => new ArgumentException("Array type must be array");
        public static Exception CannotAutoInitializeValueTypeElementThroughProperty(object a) => E("CannotAutoInitializeValueTypeElementThroughProperty", a);
        public static Exception CannotAutoInitializeValueTypeMemberThroughProperty(object a) => E("CannotAutoInitializeValueTypeMemberThroughProperty", a);
        public static Exception CannotCloseOverByRef(object a, object b) => E("CannotCloseOverByRef", a, b);
        public static Exception CannotCompileConstant(object a) => new NotSupportedException(
            "CompileToMethod cannot compile constant '" + a + "' (" + a?.GetType().FullName + ") because it is a non-trivial value, such as a live object. Instead, create an expression tree that can construct this value.");
        public static Exception CannotCompileDynamic() => new NotSupportedException("Dynamic operations can only be performed in homogenous AppDomain / must be rewritten before CompileToMethod.");
        public static Exception CoalesceUsedOnNonNullType() => E("CoalesceUsedOnNonNullType");
        public static Exception ControlCannotEnterExpression() => E("Control cannot enter an expression--only statements can be jumped into.");
        public static Exception ControlCannotEnterTry() => E("Control cannot enter a try block.");
        public static Exception ControlCannotLeaveFilterTest() => E("Control cannot leave a filter test.");
        public static Exception ControlCannotLeaveFinally() => E("Control cannot leave a finally block.");
        public static Exception CountCannotBeNegative() => new ArgumentException("Count must be non-negative.");
        public static Exception ExtensionNotReduced() => E("Extension node must reduce");
        public static Exception IllegalNewGenericParams(object a) => E("IllegalNewGenericParams", a);
        public static Exception IncorrectNumberOfIndexes() => new ArgumentException("Incorrect number of indexes");
        public static Exception InvalidCast(object a, object b) => E("InvalidCast", a, b);
        public static Exception InvalidLvalue(object a) => E("InvalidLvalue", a);
        public static Exception InvalidMemberType(object a) => E("InvalidMemberType", a);
        public static Exception LabelTargetAlreadyDefined(object a) => E("LabelTargetAlreadyDefined", a);
        public static Exception LabelTargetUndefined(object a) => E("LabelTargetUndefined", a);
        public static Exception MemberNotFieldOrProperty(object a) => E("MemberNotFieldOrProperty", a);
        public static Exception NonLocalJumpWithValue(object a) => E("NonLocalJumpWithValue", a);
        public static Exception OperatorNotImplementedForType(object a, object b) => E("OperatorNotImplementedForType", a, b);
        public static Exception QueueEmpty() => E("Queue empty");
        public static Exception RethrowRequiresCatch() => E("Rethrow statement is valid only inside a Catch block.");
        public static Exception TryNotAllowedInFilter() => E("Try expression is not allowed inside a filter body.");
        public static Exception TryNotSupportedForMethodsWithRefArgs(object a) => E("TryNotSupportedForMethodsWithRefArgs", a);
        public static Exception TryNotSupportedForValueTypeInstances(object a) => E("TryNotSupportedForValueTypeInstances", a);
        public static Exception TypeContainsGenericParameters(object a) => E("TypeContainsGenericParameters", a);
        public static Exception TypeDoesNotHaveConstructorForTheSignature() => E("TypeDoesNotHaveConstructorForTheSignature");
        public static Exception TypeIsGeneric(object a) => E("TypeIsGeneric", a);
        public static Exception UndefinedVariable(object a, object b, object c) => E("UndefinedVariable", a, b, c);
        public static Exception UnexpectedCoalesceOperator() => E("UnexpectedCoalesceOperator");
        public static Exception UnexpectedVarArgsCall(object a) => E("UnexpectedVarArgsCall", a);
        public static Exception UnhandledBinary(object a) => E("UnhandledBinary", a);
        public static Exception UnhandledBinding() => E("UnhandledBinding");
        public static Exception UnhandledConvert(object a) => E("UnhandledConvert", a);
        public static Exception UnhandledUnary(object a) => E("UnhandledUnary", a);
        public static Exception UnknownBindingType() => E("UnknownBindingType");
        public static Exception UnknownLiftType(object a) => E("UnknownLiftType", a);
    }
}

// Closure and RuntimeOps, which the saved IL references, live in IronRuby.dll (IronRuby.Runtime.Precompiled)
// so that a saved assembly needs nothing but IronRuby at run time.

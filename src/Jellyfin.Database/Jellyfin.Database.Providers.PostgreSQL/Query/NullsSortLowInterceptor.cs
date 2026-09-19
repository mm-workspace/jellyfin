using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Jellyfin.Database.Providers.PostgreSQL.Query;

/// <summary>
/// Makes orderings place NULL below every other value, as SQLite does.
/// </summary>
/// <remarks>
/// PostgreSQL sorts NULL above every other value. Every ordering on a key that can be NULL is preceded by an
/// ordering on whether the key is NULL, in the same direction, so NULL comes first in ascending and last in
/// descending order. Keys that cannot be NULL, such as required columns, are left alone. A key of a non-nullable
/// type is still NULL in SQL when it reads a row that may not exist: a member of <c>FirstOrDefault()</c> or of an
/// optional navigation, or the maximum of an empty set.
/// </remarks>
internal sealed class NullsSortLowInterceptor : IQueryExpressionInterceptor
{
    private static readonly MethodInfo _orderBy = QueryableMethod(nameof(Queryable.OrderBy));
    private static readonly MethodInfo _orderByDescending = QueryableMethod(nameof(Queryable.OrderByDescending));
    private static readonly MethodInfo _thenBy = QueryableMethod(nameof(Queryable.ThenBy));
    private static readonly MethodInfo _thenByDescending = QueryableMethod(nameof(Queryable.ThenByDescending));

    // SQL gives NULL for each of these on an empty sequence. Only when one ends the whole query can First() and the like throw instead.
    private static readonly HashSet<string> _elementOperators = new(StringComparer.Ordinal)
    {
        nameof(Queryable.First),
        nameof(Queryable.FirstOrDefault),
        nameof(Queryable.Single),
        nameof(Queryable.SingleOrDefault),
        nameof(Queryable.Last),
        nameof(Queryable.LastOrDefault),
        nameof(Queryable.ElementAt),
        nameof(Queryable.ElementAtOrDefault)
    };

    // SQL computes these as NULL over an empty set. EF Core turns an empty SUM into 0, and COUNT is never NULL.
    private static readonly HashSet<string> _emptySetAggregates = new(StringComparer.Ordinal)
    {
        nameof(Queryable.Max),
        nameof(Queryable.Min),
        nameof(Queryable.Average)
    };

    private NullsSortLowInterceptor()
    {
    }

    /// <summary>
    /// Gets the interceptor. EF Core keys its internal services on query interceptors, so every context shares this one.
    /// </summary>
    public static NullsSortLowInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        => Rewrite(queryExpression, eventData.Context?.Model);

    /// <summary>
    /// Precedes every ordering of a query on a key that can be NULL by an ordering on whether the key is NULL.
    /// </summary>
    /// <param name="queryExpression">The query expression.</param>
    /// <param name="model">The model of the context the query runs on, or <c>null</c> if there is none.</param>
    /// <returns>The rewritten query expression, or the same instance when no ordering needed it.</returns>
    internal static Expression Rewrite(Expression queryExpression, IModel? model)
        => new OrderingRewriter(model).Visit(queryExpression);

    private static MethodInfo QueryableMethod(string name)
        => typeof(Queryable).GetMethods().Single(m => m.Name == name && m.GetParameters().Length == 2);

    private static bool IsNonNullableValueType(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null;

    private static bool IsSequenceOperator(MethodCallExpression call, HashSet<string> names)
        => (call.Method.DeclaringType == typeof(Queryable) || call.Method.DeclaringType == typeof(Enumerable))
            && names.Contains(call.Method.Name);

    // Operators that can give NULL when an operand is NULL. Comparisons are not among them: EF Core compares NULL
    // as C# does, so a comparison is never NULL itself.
    private static bool PropagatesNull(ExpressionType nodeType)
        => nodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Not or ExpressionType.OnesComplement
            or ExpressionType.Negate or ExpressionType.NegateChecked or ExpressionType.UnaryPlus
            or ExpressionType.Add or ExpressionType.AddChecked or ExpressionType.Subtract or ExpressionType.SubtractChecked
            or ExpressionType.Multiply or ExpressionType.MultiplyChecked or ExpressionType.Divide or ExpressionType.Modulo
            or ExpressionType.LeftShift or ExpressionType.RightShift
            or ExpressionType.And or ExpressionType.Or or ExpressionType.ExclusiveOr or ExpressionType.AndAlso or ExpressionType.OrElse;

    private sealed class OrderingRewriter(IModel? model) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Method.DeclaringType != typeof(Queryable) || !visited.Method.IsGenericMethod || visited.Arguments.Count != 2)
            {
                return visited;
            }

            var definition = visited.Method.GetGenericMethodDefinition();
            var isFirst = definition == _orderBy || definition == _orderByDescending;
            if (!isFirst && definition != _thenBy && definition != _thenByDescending)
            {
                return visited;
            }

            var keySelector = (LambdaExpression)StripQuotes(visited.Arguments[1]);
            if (!CanBeNull(keySelector.Body))
            {
                return visited;
            }

            // A key of a non-nullable type can only be compared with null once it is nullable.
            var key = IsNonNullableValueType(keySelector.Body.Type)
                ? Expression.Convert(keySelector.Body, typeof(Nullable<>).MakeGenericType(keySelector.Body.Type))
                : keySelector.Body;
            var descending = definition == _orderByDescending || definition == _thenByDescending;
            var nullFlag = Expression.Lambda(
                Expression.Condition(Expression.Equal(key, Expression.Constant(null, key.Type)), Expression.Constant(0), Expression.Constant(1)),
                keySelector.Parameters);
            var sourceType = visited.Method.GetGenericArguments()[0];
            var flagOrdering = isFirst
                ? (descending ? _orderByDescending : _orderBy)
                : (descending ? _thenByDescending : _thenBy);
            var withFlag = Expression.Call(flagOrdering.MakeGenericMethod(sourceType, typeof(int)), visited.Arguments[0], Expression.Quote(nullFlag));
            return Expression.Call(
                (descending ? _thenByDescending : _thenBy).MakeGenericMethod(sourceType, keySelector.ReturnType),
                withFlag,
                visited.Arguments[1]);
        }

        private static Expression StripQuotes(Expression expression)
        {
            while (expression.NodeType == ExpressionType.Quote)
            {
                expression = ((UnaryExpression)expression).Operand;
            }

            return expression;
        }

        private static Expression StripConversions(Expression expression)
        {
            while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                expression = ((UnaryExpression)expression).Operand;
            }

            return expression;
        }

        private bool CanBeNull(Expression expression)
        {
            switch (expression)
            {
                case ConstantExpression constant:
                    return constant.Value is null;

                case UnaryExpression unary when PropagatesNull(unary.NodeType):
                    return CanBeNull(unary.Operand);
                case BinaryExpression binary when PropagatesNull(binary.NodeType):
                    return CanBeNull(binary.Left) || CanBeNull(binary.Right);
                case BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce:
                    return CanBeNull(coalesce.Right);
                case ConditionalExpression conditional:
                    return CanBeNull(conditional.IfTrue) || CanBeNull(conditional.IfFalse);

                case MemberExpression { Expression: { } instance } member:
                    if (MayBeMissing(instance))
                    {
                        return true;
                    }

                    // A property of an entity is only NULL when the model allows it.
                    if (member.Member is PropertyInfo property && model?.FindEntityType(instance.Type) is { } entityType)
                    {
                        return entityType.FindProperty(property.Name)?.IsNullable ?? true;
                    }

                    // SQL has no Nullable<T>.Value; it reads the nullable value itself.
                    if (member.Member.Name == nameof(Nullable<int>.Value) && Nullable.GetUnderlyingType(instance.Type) is not null)
                    {
                        return CanBeNull(instance);
                    }

                    break;

                case MethodCallExpression call when IsSequenceOperator(call, _elementOperators):
                    return true;
                case MethodCallExpression call when IsSequenceOperator(call, _emptySetAggregates):
                    // A group is never empty, so its aggregate is only NULL when the aggregated values are.
                    if (StripConversions(call.Arguments[0]) is ParameterExpression { Type.IsGenericType: true } source
                        && source.Type.GetGenericTypeDefinition() == typeof(IGrouping<,>))
                    {
                        return call.Arguments.Count > 1 && StripQuotes(call.Arguments[1]) is LambdaExpression selector
                            ? CanBeNull(selector.Body)
                            : !IsNonNullableValueType(call.Type);
                    }

                    return true;
            }

            // Anything else is NULL only where its type allows it.
            return !IsNonNullableValueType(expression.Type);
        }

        // Whether the row an expression stands for can be absent, making every member read from it NULL.
        private bool MayBeMissing(Expression expression)
        {
            expression = StripConversions(expression);
            if (expression is MethodCallExpression call)
            {
                return IsSequenceOperator(call, _elementOperators);
            }

            if (expression is MemberExpression { Expression: { } owner } member
                && model?.FindEntityType(owner.Type)?.FindNavigation(member.Member.Name) is { IsCollection: false } navigation)
            {
                var isRequired = navigation.IsOnDependent ? navigation.ForeignKey.IsRequired : navigation.ForeignKey.IsRequiredDependent;
                return !isRequired || MayBeMissing(owner);
            }

            return false;
        }
    }
}

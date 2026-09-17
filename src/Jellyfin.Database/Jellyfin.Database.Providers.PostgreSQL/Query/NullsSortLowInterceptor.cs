using System;
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
/// descending order. Keys that cannot be NULL according to the model are left alone.
/// </remarks>
internal sealed class NullsSortLowInterceptor : IQueryExpressionInterceptor
{
    private static readonly MethodInfo _orderBy = QueryableMethod(nameof(Queryable.OrderBy));
    private static readonly MethodInfo _orderByDescending = QueryableMethod(nameof(Queryable.OrderByDescending));
    private static readonly MethodInfo _thenBy = QueryableMethod(nameof(Queryable.ThenBy));
    private static readonly MethodInfo _thenByDescending = QueryableMethod(nameof(Queryable.ThenByDescending));

    private NullsSortLowInterceptor()
    {
    }

    /// <summary>
    /// Gets the interceptor. EF Core keys its internal services on query interceptors, so every context shares this one.
    /// </summary>
    public static NullsSortLowInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        => new OrderingRewriter(eventData.Context?.Model).Visit(queryExpression);

    private static MethodInfo QueryableMethod(string name)
        => typeof(Queryable).GetMethods().Single(m => m.Name == name && m.GetParameters().Length == 2);

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

            var descending = definition == _orderByDescending || definition == _thenByDescending;
            var nullFlag = Expression.Lambda(
                Expression.Condition(Expression.Equal(keySelector.Body, Expression.Constant(null, keySelector.Body.Type)), Expression.Constant(0), Expression.Constant(1)),
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

        private bool CanBeNull(Expression expression)
        {
            while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                var operand = ((UnaryExpression)expression).Operand;
                if (operand.Type.IsValueType && Nullable.GetUnderlyingType(operand.Type) is null)
                {
                    return false;
                }

                expression = operand;
            }

            if (expression.Type.IsValueType && Nullable.GetUnderlyingType(expression.Type) is null)
            {
                return false;
            }

            // A property of an entity is only NULL when the model allows it; anything else may be.
            if (expression is MemberExpression { Member: PropertyInfo property, Expression: { } instance } && model?.FindEntityType(instance.Type) is { } entityType)
            {
                return entityType.FindProperty(property.Name)?.IsNullable ?? true;
            }

            return expression is not ConstantExpression { Value: not null };
        }
    }
}

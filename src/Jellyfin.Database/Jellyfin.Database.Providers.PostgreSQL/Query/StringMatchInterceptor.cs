using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.PostgreSQL.Query;

/// <summary>
/// Turns StartsWith and EndsWith into LIKE patterns, so that they ignore the case of ASCII letters as they do on SQLite.
/// </summary>
/// <remarks>
/// Entity Framework Core translates these methods before any translator plugin sees them. The pattern is escaped,
/// so wildcards in the value match literally.
/// </remarks>
internal sealed class StringMatchInterceptor : IQueryExpressionInterceptor
{
    private static readonly MethodInfo _startsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo _endsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo _like = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;
    private static readonly MethodInfo _replace = typeof(string).GetMethod(nameof(string.Replace), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo _concat = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!;

    private StringMatchInterceptor()
    {
    }

    /// <summary>
    /// Gets the interceptor. EF Core keys its internal services on query interceptors, so every context shares this one.
    /// </summary>
    public static StringMatchInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        => new Rewriter().Visit(queryExpression);

    private sealed class Rewriter : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Object is null || (visited.Method != _startsWith && visited.Method != _endsWith))
            {
                return visited;
            }

            Expression escaped = visited.Arguments[0];
            foreach (var character in new[] { "\\", "%", "_" })
            {
                escaped = Expression.Call(escaped, _replace, Expression.Constant(character), Expression.Constant("\\" + character));
            }

            // The same shape the compiler produces for string +, which Entity Framework Core translates.
            var pattern = visited.Method == _startsWith
                ? Expression.Add(escaped, Expression.Constant("%"), _concat)
                : Expression.Add(Expression.Constant("%"), escaped, _concat);
            return Expression.Call(_like, Expression.Constant(EF.Functions), visited.Object, pattern, Expression.Constant("\\"));
        }
    }
}

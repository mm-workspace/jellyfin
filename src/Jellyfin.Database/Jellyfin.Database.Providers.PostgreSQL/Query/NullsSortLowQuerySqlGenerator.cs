// How a provider writes SQL is part of Entity Framework Core's infrastructure, so extending Npgsql's generator is the
// only way to reach the ORDER BY clause. Npgsql moving or renaming it breaks the build rather than the ordering.
#pragma warning disable EF1001 // Internal EF Core API usage.

using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace Jellyfin.Database.Providers.PostgreSQL.Query;

/// <summary>
/// Writes orderings that place NULL below every other value, as SQLite does.
/// </summary>
/// <remarks>
/// PostgreSQL sorts NULL above every other value, and its defaults are <c>ASC NULLS LAST</c> and
/// <c>DESC NULLS FIRST</c>, so an ordering on a key that can be NULL states the other option in both directions:
/// <c>ASC NULLS FIRST</c> and <c>DESC NULLS LAST</c>. That is what the ordering means on SQLite, and unlike a second
/// ordering on whether the key is NULL it leaves the ORDER BY one expression per key, so an index declared with the
/// same option can serve it. The option is left out only where the key is provably not NULL, because there PostgreSQL
/// already orders as SQLite does and an ordinary index serves the ordering.
/// </remarks>
internal sealed class NullsSortLowQuerySqlGenerator : NpgsqlQuerySqlGenerator
{
    private readonly bool _reverseNullOrderingEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="NullsSortLowQuerySqlGenerator"/> class.
    /// </summary>
    /// <param name="dependencies">The generator's dependencies.</param>
    /// <param name="typeMappingSource">The type mapping source.</param>
    /// <param name="reverseNullOrderingEnabled">Whether Npgsql was asked to reverse NULL ordering by itself.</param>
    /// <param name="postgresVersion">The server version the SQL is written for.</param>
    public NullsSortLowQuerySqlGenerator(
        QuerySqlGeneratorDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        bool reverseNullOrderingEnabled,
        Version postgresVersion)
        : base(dependencies, typeMappingSource, reverseNullOrderingEnabled, postgresVersion)
    {
        _reverseNullOrderingEnabled = reverseNullOrderingEnabled;
    }

    /// <inheritdoc />
    protected override Expression VisitOrdering(OrderingExpression orderingExpression)
    {
        var visited = base.VisitOrdering(orderingExpression);

        // Npgsql writes the same two options itself when it was asked to reverse NULL ordering.
        if (!_reverseNullOrderingEnabled && CanBeNull(orderingExpression.Expression))
        {
            Sql.Append(orderingExpression.IsAscending ? " NULLS FIRST" : " NULLS LAST");
        }

        return visited;
    }

    /// <summary>
    /// Checks whether an ordering key can be NULL.
    /// </summary>
    /// <remarks>
    /// Only the keys an index can serve are worth proving anything about, so anything that is not one of those counts
    /// as NULL: the option is then written where it was not needed, which orders the same rows the same way.
    /// </remarks>
    /// <param name="expression">The ordering key.</param>
    /// <returns><c>true</c> unless the key is provably not NULL.</returns>
    private static bool CanBeNull(SqlExpression expression) => expression switch
    {
        // A column of the nullable side of an outer join reads as NULL for a row that side does not have, and Entity
        // Framework Core marks it nullable for that reason even when its own table declares it as not nullable.
        ColumnExpression column => column.IsNullable,
        SqlConstantExpression constant => constant.Value is null,
        SqlParameterExpression parameter => parameter.IsNullable,

        // IS NULL and IS NOT NULL, which are never NULL themselves.
        SqlUnaryExpression { OperatorType: ExpressionType.Equal or ExpressionType.NotEqual } => false,
        _ => true
    };
}

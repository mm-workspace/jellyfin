using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Database.Providers.PostgreSQL.Query;

/// <summary>
/// Translates <c>Min</c> and <c>Max</c> over <see cref="Guid"/> values, which PostgreSQL has no aggregate for.
/// </summary>
/// <remarks>
/// The aggregate runs over the text form under the "C" collation and is cast back. Text in canonical form sorts
/// exactly like the uuid bytes, and like the ids SQLite stores as text, so both databases pick the same id.
/// </remarks>
internal sealed class UuidMinMaxTranslator : IAggregateMethodCallTranslator
{
    private readonly ISqlExpressionFactory _sqlExpressionFactory;
    private readonly RelationalTypeMapping _textTypeMapping;
    private readonly RelationalTypeMapping _uuidTypeMapping;

    /// <summary>
    /// Initializes a new instance of the <see cref="UuidMinMaxTranslator"/> class.
    /// </summary>
    /// <param name="sqlExpressionFactory">The SQL expression factory.</param>
    /// <param name="typeMappingSource">The type mapping source.</param>
    public UuidMinMaxTranslator(ISqlExpressionFactory sqlExpressionFactory, IRelationalTypeMappingSource typeMappingSource)
    {
        _sqlExpressionFactory = sqlExpressionFactory;
        _textTypeMapping = typeMappingSource.FindMapping(typeof(string))!;
        _uuidTypeMapping = typeMappingSource.FindMapping(typeof(Guid))!;
    }

    /// <inheritdoc />
    public SqlExpression? Translate(MethodInfo method, EnumerableExpression source, IReadOnlyList<SqlExpression> arguments, IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(Queryable) || !method.IsGenericMethod || source.Selector is not SqlExpression selector)
        {
            return null;
        }

        if ((Nullable.GetUnderlyingType(selector.Type) ?? selector.Type) != typeof(Guid))
        {
            return null;
        }

        var definition = method.GetGenericMethodDefinition();
        var function = definition == QueryableMethods.MinWithSelector || definition == QueryableMethods.MinWithoutSelector ? "MIN"
            : definition == QueryableMethods.MaxWithSelector || definition == QueryableMethods.MaxWithoutSelector ? "MAX"
            : null;
        if (function is null)
        {
            return null;
        }

        SqlExpression text = new CollateExpression(_sqlExpressionFactory.Convert(selector, typeof(string), _textTypeMapping), PostgreSqlDatabaseProvider.BinaryCollation);
        if (source.Predicate is not null)
        {
            text = _sqlExpressionFactory.Case([new CaseWhenClause(source.Predicate, text)], elseResult: null);
        }

        if (source.IsDistinct)
        {
            text = new DistinctExpression(text);
        }

        var aggregate = _sqlExpressionFactory.Function(function, [text], nullable: true, argumentsPropagateNullability: [false], typeof(string), _textTypeMapping);
        return _sqlExpressionFactory.Convert(aggregate, method.ReturnType, _uuidTypeMapping);
    }
}

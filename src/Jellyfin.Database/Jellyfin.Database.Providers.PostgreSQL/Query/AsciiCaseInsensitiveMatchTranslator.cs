using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Database.Providers.PostgreSQL.Query;

/// <summary>
/// Translates LIKE so that it ignores the case of ASCII letters, as it does on SQLite.
/// </summary>
/// <remarks>
/// Both sides are lowered under the "C" collation, which only lowers ASCII letters, like SQLite's case folding.
/// Columns already use that collation, so an index on lower(column) still matches.
/// </remarks>
internal sealed class AsciiCaseInsensitiveMatchTranslator : IMethodCallTranslator
{
    private static readonly MethodInfo _like = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string)])!;
    private static readonly MethodInfo _likeWithEscape = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    private readonly ISqlExpressionFactory _sqlExpressionFactory;
    private readonly RelationalTypeMapping _textTypeMapping;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsciiCaseInsensitiveMatchTranslator"/> class.
    /// </summary>
    /// <param name="sqlExpressionFactory">The SQL expression factory.</param>
    /// <param name="typeMappingSource">The type mapping source.</param>
    public AsciiCaseInsensitiveMatchTranslator(ISqlExpressionFactory sqlExpressionFactory, IRelationalTypeMappingSource typeMappingSource)
    {
        _sqlExpressionFactory = sqlExpressionFactory;
        _textTypeMapping = typeMappingSource.FindMapping(typeof(string))!;
    }

    /// <inheritdoc />
    public SqlExpression? Translate(SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments, IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method == _like || method == _likeWithEscape)
        {
            // SQLite has no LIKE escape character unless one is given; PostgreSQL uses backslash by default.
            var escape = method == _likeWithEscape
                ? _sqlExpressionFactory.ApplyTypeMapping(arguments[3], _textTypeMapping)
                : _sqlExpressionFactory.Constant(string.Empty, _textTypeMapping);
            return new LikeExpression(Lower(arguments[1]), Lower(arguments[2]), escape, _textTypeMapping);
        }

        return null;
    }

    private SqlExpression Lower(SqlExpression value)
    {
        // Column values already sort and lower under "C"; other values would otherwise use the database collation.
        var operand = value is ColumnExpression ? value : new CollateExpression(_sqlExpressionFactory.ApplyTypeMapping(value, _textTypeMapping), PostgreSqlDatabaseProvider.BinaryCollation);
        return _sqlExpressionFactory.Function("lower", [operand], nullable: true, argumentsPropagateNullability: [true], typeof(string), _textTypeMapping);
    }
}

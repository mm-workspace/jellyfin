// Deriving from Npgsql's factory keeps the generator in step with whatever else Npgsql decides to hand it.
#pragma warning disable EF1001 // Internal EF Core API usage.

using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace Jellyfin.Database.Providers.PostgreSQL.Query;

/// <summary>
/// Hands out the <see cref="NullsSortLowQuerySqlGenerator"/> in place of Npgsql's own generator.
/// </summary>
internal sealed class NullsSortLowQuerySqlGeneratorFactory : NpgsqlQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly INpgsqlSingletonOptions _npgsqlSingletonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="NullsSortLowQuerySqlGeneratorFactory"/> class.
    /// </summary>
    /// <param name="dependencies">The generator's dependencies.</param>
    /// <param name="typeMappingSource">The type mapping source.</param>
    /// <param name="npgsqlSingletonOptions">The options of the Npgsql provider.</param>
    public NullsSortLowQuerySqlGeneratorFactory(
        QuerySqlGeneratorDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, typeMappingSource, npgsqlSingletonOptions)
    {
        _dependencies = dependencies;
        _typeMappingSource = typeMappingSource;
        _npgsqlSingletonOptions = npgsqlSingletonOptions;
    }

    /// <inheritdoc />
    public override QuerySqlGenerator Create()
        => new NullsSortLowQuerySqlGenerator(
            _dependencies,
            _typeMappingSource,
            _npgsqlSingletonOptions.ReverseNullOrderingEnabled,
            _npgsqlSingletonOptions.PostgresVersion);
}

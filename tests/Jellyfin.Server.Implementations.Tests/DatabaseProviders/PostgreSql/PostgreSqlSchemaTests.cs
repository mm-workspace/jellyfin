using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

[Trait("Provider", "PostgreSql")]
public sealed class PostgreSqlSchemaTests : IDisposable
{
    private const string CatalogQuery = """
        SELECT 'column ' || c.table_name || '.' || c.column_name || ' ' || c.data_type || ' null=' || c.is_nullable
               || ' collation=' || coalesce(c.collation_name, '') || ' identity=' || c.is_identity || ' default=' || coalesce(c.column_default, '')
        FROM information_schema.columns c WHERE c.table_schema = current_schema()
        UNION ALL
        SELECT 'constraint ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid)
        FROM pg_constraint WHERE connamespace = current_schema()::regnamespace
        UNION ALL
        SELECT 'index ' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = current_schema()
        """;

    private readonly PostgreSqlTestDatabase? _migrated;

    public PostgreSqlSchemaTests()
    {
        if (TestDatabase.PostgreSqlConnectionString is { } connectionString)
        {
            _migrated = new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions());
        }
    }

    private PostgreSqlTestDatabase Migrated
    {
        get
        {
            Assert.SkipWhen(_migrated is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
            return _migrated;
        }
    }

    [Fact]
    public async Task Migrations_ProduceTheModelSchema()
    {
        var migrated = await ReadCatalogAsync(Migrated.ConnectionString);

        var builder = new NpgsqlConnectionStringBuilder(TestDatabase.PostgreSqlConnectionString) { Database = Migrated.DatabaseName + "_ec" };
        await ExecuteOnServerAsync($"CREATE DATABASE \"{builder.Database}\" TEMPLATE template0 ENCODING 'UTF8'");
        try
        {
            var options = new DbContextOptionsBuilder<JellyfinDbContext>(Migrated.Options).UseNpgsql(builder.ConnectionString).Options;
            await using (var context = new JellyfinDbContext(options, NullLogger<JellyfinDbContext>.Instance, Migrated.Provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)))
            {
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }

            var created = await ReadCatalogAsync(builder.ConnectionString);

            var onlyMigrated = migrated.Except(created).Where(e => !e.Contains("__EFMigrationsHistory", StringComparison.Ordinal)).ToArray();
            var onlyCreated = created.Except(migrated).ToArray();

            // The expression index is the one object the model cannot declare.
            Assert.Equal(["index IX_Peoples_NameLower CREATE INDEX \"IX_Peoples_NameLower\" ON public.\"Peoples\" USING btree (lower(\"Name\"))"], onlyMigrated);
            Assert.Empty(onlyCreated);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{builder.Database}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task PeopleNameLowerIndex_IsUsedForLowerNameLookups()
    {
        await using var connection = new NpgsqlConnection(Migrated.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SET enable_seqscan = off; EXPLAIN SELECT * FROM \"Peoples\" p WHERE lower(p.\"Name\") = 'someone'", connection);
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    plan.Add(reader.GetString(0));
                }
            }
            while (await reader.NextResultAsync(TestContext.Current.CancellationToken));
        }

        Assert.Contains(plan, line => line.Contains("IX_Peoples_NameLower", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _migrated?.Dispose();
    }

    private static async Task<HashSet<string>> ReadCatalogAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(CatalogQuery, connection);
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Statements are built from generated database names.")]
    private static async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.PostgreSqlConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

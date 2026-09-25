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

            // The expression indexes are the objects the model cannot declare.
            Assert.Equal(
                [
                    "index IX_BaseItems_VersionGroup CREATE INDEX \"IX_BaseItems_VersionGroup\" ON public.\"BaseItems\" USING btree (COALESCE(\"PrimaryVersionId\", \"Id\"))",
                    "index IX_Peoples_NameLower CREATE INDEX \"IX_Peoples_NameLower\" ON public.\"Peoples\" USING btree (lower(\"Name\"))"
                ],
                onlyMigrated.Order(System.StringComparer.Ordinal));
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
        var plan = await ExplainAsync("SELECT * FROM \"Peoples\" p WHERE lower(p.\"Name\") = 'someone'");

        Assert.Contains(plan, line => line.Contains("IX_Peoples_NameLower", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SortNameIndex_IsUsedForTheOrderTheBrowsePagesAskFor()
    {
        // The provider writes NULLS FIRST for an ascending ordering on a key that can be NULL, and only an index
        // declared the same way delivers the rows in that order. Reading it backwards covers the descending direction.
        // Both keys, as ApplyOrder writes them for a browse page: the index delivers the sort name and an
        // incremental sort finishes the groups the name breaks, so the page never sorts the whole table.
        var plan = await ExplainAsync(
            "SELECT b.\"Id\" FROM \"BaseItems\" b WHERE b.\"Type\" = 'Movie' AND b.\"TopParentId\" = '00000000-0000-0000-0000-000000000002'"
            + " ORDER BY b.\"SortName\" NULLS FIRST, b.\"Name\" NULLS FIRST LIMIT 10");

        Assert.Contains(plan, line => line.Contains("IX_BaseItems_Type_TopParentId_SortName", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("Seq Scan", StringComparison.Ordinal));

        // The rows arrive in the index's order and only each sort-name group is finished. An incremental sort
        // can only appear when its input is already sorted, so its presence is what rules out a full sort.
        Assert.Contains(plan, line => line.Contains("Incremental Sort", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.Contains("Presorted Key", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _migrated?.Dispose();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements are constants.")]
    private async Task<List<string>> ExplainAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Migrated.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // The tables are empty, so only the planner's own preferences decide anything without this.
        await using var command = new NpgsqlCommand("SET enable_seqscan = off; EXPLAIN " + sql, connection);
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        do
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                plan.Add(reader.GetString(0));
            }
        }
        while (await reader.NextResultAsync(TestContext.Current.CancellationToken));

        return plan;
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

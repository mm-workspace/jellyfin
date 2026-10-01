using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Database.Testing;
using Jellyfin.Database.Testing.Synthetic;
using Jellyfin.Server.Implementations.DatabaseImport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport;

public sealed class SyntheticLibraryTests : IDisposable
{
    private static readonly KeyValuePair<string, string>[] _codeMigrations = [new("20990101000000_SyntheticCodeMigration", "10.12.0.0")];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jf-synthetic-" + Guid.NewGuid().ToString("N"));

    public SyntheticLibraryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task CreateSqliteDatabase_FillsEveryTableConsistently()
    {
        var path = await CreateAsync("small", SyntheticLibraryOptions.Small);

        await using var connection = await OpenAsync(path);
        Assert.All(ImportModel.ForSqlite().Tables, table => Assert.True(Scalar<long>(connection, $"SELECT count(*) FROM \"{table.Name}\"") > 0, table.Name));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT count(*) FROM pragma_foreign_key_check"));
        Assert.Equal("ok", Scalar<string>(connection, "PRAGMA integrity_check"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20990101000000_SyntheticCodeMigration'"));
        Assert.Equal(1000L, Scalar<long>(connection, "SELECT count(*) FROM \"BaseItems\" WHERE \"Id\" <> '00000000-0000-0000-0000-000000000001'"));
    }

    [Fact]
    public async Task CreateSqliteDatabase_StoresValuesLikeTheServer()
    {
        var path = await CreateAsync("small", SyntheticLibraryOptions.Small);

        await using var connection = await OpenAsync(path);
        var storage = Scalar<string>(
            connection,
            "SELECT typeof(\"Id\") || ' ' || typeof(\"IsFolder\") || ' ' || typeof(\"DateCreated\") || ' ' || typeof(\"CommunityRating\") FROM \"BaseItems\" WHERE \"DateCreated\" IS NOT NULL AND \"CommunityRating\" IS NOT NULL LIMIT 1");
        Assert.Equal("text integer text real", storage);
        Assert.Equal(0L, Scalar<long>(connection, "SELECT count(*) FROM \"BaseItems\" WHERE \"Id\" <> upper(\"Id\") OR length(\"Id\") <> 36"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT count(*) FROM \"BaseItems\" WHERE \"DateCreated\" NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9] [0-9][0-9]:[0-9][0-9]:[0-9][0-9]*'"));
    }

    [Fact]
    public async Task CreateSqliteDatabase_SameOptions_SameContent()
    {
        var first = await HashesAsync(await CreateAsync("first", SyntheticLibraryOptions.Small));
        var second = await HashesAsync(await CreateAsync("second", SyntheticLibraryOptions.Small));
        var otherSeed = await HashesAsync(await CreateAsync("other", SyntheticLibraryOptions.Small with { Seed = 2 }));

        Assert.Equal(first, second);
        Assert.NotEqual(first["BaseItems"], otherSeed["BaseItems"]);
    }

    [Fact]
    public async Task CreateSqliteDatabase_EdgeValues_ReadableByTheServerModel()
    {
        var path = await CreateAsync("edge", SyntheticLibraryOptions.SmallEdge);

        await using var context = CreateSqliteContext(path);
        var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;
        foreach (var entityType in context.Model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            var rows = ((IEnumerable)set.MakeGenericMethod(entityType.ClrType).Invoke(context, null)!).Cast<object>().Count();
            Assert.True(rows > 0, entityType.DisplayName());
            context.ChangeTracker.Clear();
        }
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task PopulatePostgreSql_SameOptions_HashesLikeTheSqliteSource()
    {
        Assert.SkipWhen(TestDatabase.PostgreSqlConnectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
        var sqlite = await HashesAsync(await CreateAsync("edge", SyntheticLibraryOptions.SmallEdge));

        using var database = new PostgreSqlTestDatabase(TestDatabase.PostgreSqlConnectionString!, new TestDatabaseOptions());
        await using (var context = database.CreateDbContext())
        {
            await SyntheticLibrary.PopulateAsync(context, SyntheticLibraryOptions.SmallEdge, TestContext.Current.CancellationToken);
        }

        var postgreSql = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var context = database.CreateDbContext())
        {
            await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            foreach (var table in ImportModel.ForPostgreSql().Tables)
            {
                postgreSql[table.Name] = (await TableContentHash.ComputeAsync(context.Database.GetDbConnection(), table, TestContext.Current.CancellationToken)).Value;
            }
        }

        Assert.Equal(sqlite, postgreSql);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The statements are constants of this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        return (T)command.ExecuteScalar()!;
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<Dictionary<string, string>> HashesAsync(string path)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(path);
        foreach (var table in ImportModel.ForPostgreSql().Tables)
        {
            hashes[table.Name] = (await TableContentHash.ComputeAsync(connection, table, TestContext.Current.CancellationToken)).Value;
        }

        return hashes;
    }

    private static JellyfinDbContext CreateSqliteContext(string path)
    {
        var provider = new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(builder, new DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-SQLite",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = string.Empty,
                Options = { new CustomDatabaseOption { Key = "path", Value = path }, new CustomDatabaseOption { Key = "pooling", Value = "false" } }
            }
        });
        return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private async Task<string> CreateAsync(string name, SyntheticLibraryOptions options)
    {
        var path = Path.Combine(_directory, name, "jellyfin.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await SyntheticLibrary.CreateSqliteDatabaseAsync(path, options, _codeMigrations, TestContext.Current.CancellationToken);
        return path;
    }
}

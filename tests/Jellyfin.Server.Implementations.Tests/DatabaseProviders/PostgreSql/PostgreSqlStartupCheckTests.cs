using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Providers.PostgreSQL.Migrations;
using Jellyfin.Database.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

[Trait("Provider", "PostgreSql")]
public sealed class PostgreSqlStartupCheckTests : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _databases = [];
    private readonly List<string> _roles = [];
    private readonly RecordingLogger _logger = new();

    private static string ServerConnectionString
    {
        get
        {
            Assert.SkipWhen(TestDatabase.PostgreSqlConnectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
            return TestDatabase.PostgreSqlConnectionString;
        }
    }

    [Theory]
    [InlineData("15.8", false)]
    [InlineData("15.99", false)]
    [InlineData("16.0", true)]
    [InlineData("18.1", true)]
    public void IsSupportedServerVersion_RequiresPostgreSql16(string version, bool supported)
    {
        Assert.Equal(supported, PostgreSqlStartupChecks.IsSupportedServerVersion(Version.Parse(version)));
    }

    [Theory]
    [InlineData(false, SslMode.Prefer, "127.0.0.1", null)]
    [InlineData(false, SslMode.Disable, "192.168.1.5", null)]
    [InlineData(false, SslMode.Prefer, "postgres", null)]
    [InlineData(false, SslMode.Disable, "db.example.com", "not encrypted")]
    [InlineData(true, SslMode.Require, "db.example.com", "not verified")]
    [InlineData(true, SslMode.VerifyFull, "db.example.com", null)]
    public void GetTransportWarning_WarnsOnlyForRemoteHosts(bool encrypted, SslMode sslMode, string host, string? expected)
    {
        var warning = PostgreSqlStartupChecks.GetTransportWarning(encrypted, sslMode, host);

        if (expected is null)
        {
            Assert.Null(warning);
        }
        else
        {
            Assert.NotNull(warning);
            Assert.Contains(expected, warning, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NonUtf8Database_IsRefused()
    {
        var database = CreateDatabase("TEMPLATE template0 ENCODING 'SQL_ASCII' LC_COLLATE 'C' LC_CTYPE 'C'");
        await using var context = CreateContext(BuildOptions(database));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("SQL_ASCII", exception.Message, StringComparison.Ordinal);
        Assert.Contains("UTF8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPathWithoutHistory_HistoryInOtherSchema_IsRefused()
    {
        var database = CreateDatabase();
        await using (var context = CreateContext(BuildOptions(database)))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(database, "CREATE SCHEMA jellyfin_other");
        await using var otherContext = CreateContext(BuildOptions(database, b => b.SearchPath = "jellyfin_other"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => otherContext.Database.OpenConnectionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("schema public", exception.Message, StringComparison.Ordinal);
        Assert.Contains("schema jellyfin_other", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPathWithoutExistingSchema_IsRefused()
    {
        var database = CreateDatabase();
        await using var context = CreateContext(BuildOptions(database, b => b.SearchPath = "missing_schema"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("search_path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignObjects_DatabaseWithoutHistory_AreRefusedAndNamed()
    {
        var database = CreateDatabase();
        await ExecuteAsync(
            database,
            """
            CREATE TABLE "Series" (id int);
            CREATE TABLE numbered (id serial PRIMARY KEY);
            CREATE SEQUENCE counter;
            CREATE VIEW answer AS SELECT 42 AS value;
            CREATE TYPE mood AS ENUM ('fine');
            CREATE FUNCTION add_one(i int) RETURNS int LANGUAGE sql AS 'SELECT i + 1';
            """);
        await using var context = CreateContext(BuildOptions(database));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("function \"add_one\", sequence \"counter\", table \"Series\", table \"numbered\", type \"mood\" and 1 more", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("numbered_id_seq", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pkey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedChecks_RunAgainOnTheNextConnection()
    {
        var database = CreateDatabase();
        await ExecuteAsync(database, "CREATE TABLE leftover (id int)");
        var options = BuildOptions(database);

        await using (var context = CreateContext(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken));
        }

        await ExecuteAsync(database, "DROP TABLE leftover");
        await using (var context = CreateContext(options))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ExtensionObjects_AreIgnored()
    {
        var database = CreateDatabase();
        await ExecuteAsync(database, "CREATE EXTENSION pg_trgm");
        await using var context = CreateContext(BuildOptions(database));

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ModelTablesWithoutHistory_AreAccepted()
    {
        var database = CreateDatabase();
        await using (var context = CreateContext(BuildOptions(database)))
        {
            Assert.True(await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken));
        }

        await using var checkedContext = CreateContext(BuildOptions(database));
        await checkedContext.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await checkedContext.Database.CloseConnectionAsync();
    }

    [Fact]
    public async Task PluginMigrationHistory_IsRefused()
    {
        var database = await CreateDatabaseWithHistoryAsync("20250618214615_PgSQL_Init");
        await using var context = CreateContext(BuildOptions(database));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("PostgreSQL plugin", exception.Message, StringComparison.Ordinal);
        Assert.Contains("20250618214615_PgSQL_Init", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqliteMigrationHistory_IsRefused()
    {
        var database = await CreateDatabaseWithHistoryAsync(PostgreSqlBaselineSquashedIds.Ids[0]);
        await using var context = CreateContext(BuildOptions(database));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("SQLite migration " + PostgreSqlBaselineSquashedIds.Ids[0], exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingDatabase_IsCreatedByMigrations()
    {
        var database = "jf_sc_" + _id + "_" + (_databases.Count + 1);
        _databases.Add(database);
        await using var context = CreateContext(BuildOptions(database));

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Superuser_IsWarnedAndConnectionSummaryLogged()
    {
        var database = CreateDatabase();
        await using var context = CreateContext(BuildOptions(database));

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("is a superuser", StringComparison.Ordinal));
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("PostgreSQL ", StringComparison.Ordinal) && e.Message.Contains("JIT off, maximum pool size", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("JIT compilation is on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JitLeftOnByConfiguration_IsWarned()
    {
        var database = CreateDatabase();
        await using var context = CreateContext(BuildOptions(database, b => b.Options = "-c jit=on", ("jit", "server")));

        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.CloseConnectionAsync();

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("JIT compilation is on", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("4MB", "8192", "may use 4000 MB (work_mem 4MB x hash_mem_multiplier 1000), less than the 8192 MB", "work_mem = '9MB'")]
    [InlineData("64kB", "100", "may use 62 MB (work_mem 64kB x hash_mem_multiplier 1000), less than the 100 MB", "work_mem = '1MB'")]
    public async Task HashMemoryBeyondThousandTimesWorkMem_IsWarned(string workMem, string hashMemory, string expected, string suggestedWorkMem)
    {
        var database = CreateDatabase();
        ExecuteOnServer($"ALTER DATABASE \"{database}\" SET work_mem = '{workMem}'");
        await using var context = CreateContext(BuildOptions(database, null, ("hash-memory", hashMemory)));

        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.CloseConnectionAsync();

        var warning = Assert.Single(_logger.Entries, e => e.Message.StartsWith("Hash tables", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(expected, warning.Message, StringComparison.Ordinal);
        Assert.Contains("hash_mem_multiplier cannot exceed 1000", warning.Message, StringComparison.Ordinal);
        Assert.Contains(suggestedWorkMem, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HashMemMultiplierLostByConnectionReset_IsWarnedAsLost()
    {
        var database = CreateDatabase();
        ExecuteOnServer($"ALTER DATABASE \"{database}\" SET work_mem = '4MB'");
        ExecuteOnServer($"ALTER DATABASE \"{database}\" SET hash_mem_multiplier = 2");
        await ExecuteAsync(database, "CREATE TABLE leftover (id int)");
        var options = BuildOptions(database, b =>
        {
            b.Pooling = true;
            b.NoResetOnClose = false;
        });

        // The failed check leaves the connection in the pool, which resets it, so the retried check runs without the session setup.
        await using (var context = CreateContext(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken));
        }

        await ExecuteAsync(database, "DROP TABLE leftover");
        await using (var context = CreateContext(options))
        {
            await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await context.Database.CloseConnectionAsync();
        }

        var warning = Assert.Single(_logger.Entries, e => e.Message.StartsWith("Hash tables", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("may use 8 MB (work_mem 4MB x hash_mem_multiplier 2), less than the 32 MB", warning.Message, StringComparison.Ordinal);
        Assert.Contains("no longer has the hash_mem_multiplier it set when it was opened", warning.Message, StringComparison.Ordinal);
        Assert.Contains("SET hash_mem_multiplier = 8.", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("work_mem = '", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoleThatNeedsQuoting_IsQuotedInSuggestedStatements()
    {
        var role = "jf-sc-Role-" + _id;
        var password = "pw-" + Guid.NewGuid().ToString("N");
        ExecuteOnServer($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}'");
        _roles.Add(role);
        var database = CreateDatabase($"OWNER \"{role}\" TEMPLATE template0 ENCODING 'UTF8'");
        ExecuteOnServer($"ALTER DATABASE \"{database}\" SET work_mem = '64kB'");
        var options = BuildOptions(
            database,
            b =>
            {
                b.Username = role;
                b.Password = password;
                b.Options = "-c jit=on";
            },
            ("jit", "server"),
            ("hash-memory", "100"));
        await using var context = CreateContext(options);

        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.CloseConnectionAsync();

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains($"ALTER ROLE \"{role}\" SET jit = off.", StringComparison.Ordinal));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains($"ALTER ROLE \"{role}\" SET work_mem = '1MB'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("4MB", null)]
    [InlineData("64kB", null)]
    [InlineData("4MB", "4000")]
    [InlineData("9MB", "8192")]
    [InlineData("64kB", "server")]
    public async Task HashMemoryWithinThousandTimesWorkMem_IsNotWarned(string workMem, string? hashMemory)
    {
        var database = CreateDatabase();
        ExecuteOnServer($"ALTER DATABASE \"{database}\" SET work_mem = '{workMem}'");
        await using var context = CreateContext(BuildOptions(database, null, hashMemory is null ? [] : [("hash-memory", hashMemory)]));

        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.CloseConnectionAsync();

        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(", maximum pool size ", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.StartsWith("Hash tables", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PoolLargerThanFreeConnections_IsWarned()
    {
        var database = CreateDatabase();
        await using var context = CreateContext(BuildOptions(database, b => b.MaxPoolSize = 1000));

        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.CloseConnectionAsync();

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("free connections", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TablesOwnedByAnotherRole_AreWarned()
    {
        var serverRole = new NpgsqlConnectionStringBuilder(ServerConnectionString).Username!;
        var role = "jf_sc_role_" + _id;
        var password = "pw-" + Guid.NewGuid().ToString("N");
        ExecuteOnServer($"CREATE ROLE {role} LOGIN PASSWORD '{password}'");
        _roles.Add(role);
        var database = CreateDatabase($"OWNER {role} TEMPLATE template0 ENCODING 'UTF8'");

        void AsRole(NpgsqlConnectionStringBuilder builder)
        {
            builder.Username = role;
            builder.Password = password;
        }

        await using (var context = CreateContext(BuildOptions(database, AsRole)))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(database, $"ALTER TABLE \"Users\" OWNER TO \"{serverRole}\"");
        _logger.Entries.Clear();

        await using (var context = CreateContext(BuildOptions(database, AsRole)))
        {
            await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await context.Database.CloseConnectionAsync();
        }

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("1 Jellyfin tables are not owned by the PostgreSQL role " + role, StringComparison.Ordinal) && e.Message.Contains("Users", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("superuser", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains(password, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (TestDatabase.PostgreSqlConnectionString is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        foreach (var database in _databases)
        {
            ExecuteOnServer($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }

        foreach (var role in _roles)
        {
            ExecuteOnServer($"DROP ROLE IF EXISTS \"{role}\"");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements only.")]
    private static void ExecuteOnServer(string sql)
    {
        using var connection = new NpgsqlConnection(ServerConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements only.")]
    private static async Task ExecuteAsync(string database, string sql)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = database, Pooling = false }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private string CreateDatabase(string options = "TEMPLATE template0 ENCODING 'UTF8'")
    {
        var database = "jf_sc_" + _id + "_" + (_databases.Count + 1);
        ExecuteOnServer($"CREATE DATABASE \"{database}\" {options}");
        _databases.Add(database);
        return database;
    }

    private async Task<string> CreateDatabaseWithHistoryAsync(string migrationId)
    {
        var database = CreateDatabase();
        await ExecuteAsync(
            database,
            $"""
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES ('{migrationId}', '9.0.0');
            """);
        return database;
    }

    private (PostgreSqlDatabaseProvider Provider, DbContextOptions<JellyfinDbContext> Options) BuildOptions(string database, Action<NpgsqlConnectionStringBuilder>? configure = null, params (string Key, string Value)[] providerOptions)
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = database, Pooling = false };
        configure?.Invoke(builder);

        var provider = new PostgreSqlDatabaseProvider(null!, _logger);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        var customOptions = new CustomDatabaseOptions { PluginName = string.Empty, PluginAssembly = string.Empty, ConnectionString = builder.ConnectionString };
        foreach (var (key, value) in providerOptions)
        {
            customOptions.Options.Add(new CustomDatabaseOption { Key = key, Value = value });
        }

        provider.Initialise(options, new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-PostgreSQL", CustomProviderOptions = customOptions });
        return (provider, options.Options);
    }

    private static JellyfinDbContext CreateContext((PostgreSqlDatabaseProvider Provider, DbContextOptions<JellyfinDbContext> Options) built)
        => new(built.Options, NullLogger<JellyfinDbContext>.Instance, built.Provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private sealed class RecordingLogger : ILogger<PostgreSqlDatabaseProvider>
    {
        public ConcurrentBag<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

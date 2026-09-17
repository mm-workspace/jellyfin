using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Emby.Server.Implementations;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Testing;
using Jellyfin.Database.Testing.Import;
using Jellyfin.Database.Testing.Synthetic;
using Jellyfin.Server.Configuration;
using Jellyfin.Server.DatabaseImport;
using Jellyfin.Server.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Tests.DatabaseImport;

/// <summary>
/// Runs the import steps in process against a PostgreSQL server, with a pgloader-like loader between seed and finalize.
/// </summary>
[Trait("Provider", "PostgreSql")]
public sealed class PostgreSqlImportCommandTests : IAsyncLifetime
{
    private static readonly Version _serverVersion = new(10, 12, 0, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jf-import-command-" + Guid.NewGuid().ToString("N"));
    private readonly string _databaseName = "jf_t_import_" + Guid.NewGuid().ToString("N")[..12];
    private readonly ServerApplicationPaths _paths;
    private readonly List<string> _log = [];

    public PostgreSqlImportCommandTests()
    {
        _paths = new ServerApplicationPaths(
            Path.Combine(_root, "data"),
            Path.Combine(_root, "log"),
            Path.Combine(_root, "config"),
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "web"));
    }

    private static string ServerConnectionString
    {
        get
        {
            Assert.SkipWhen(TestDatabase.PostgreSqlConnectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
            return TestDatabase.PostgreSqlConnectionString;
        }
    }

    private string SqlitePath => Path.Combine(_paths.DataPath, "jellyfin.db");

    private string ImportDirectory => Path.Combine(_paths.DataPath, "postgresql-import");

    public async ValueTask InitializeAsync()
    {
        foreach (var directory in new[] { _paths.DataPath, _paths.LogDirectoryPath, _paths.ConfigurationDirectoryPath, _paths.CachePath })
        {
            Directory.CreateDirectory(directory);
        }

        // A server of this build that ran all of its migrations.
        var codeMigrations = JellyfinMigrationService.GetCodeMigrationIds().Select(id => new KeyValuePair<string, string>(id, _serverVersion.ToString()));
        await SyntheticLibrary.CreateSqliteDatabaseAsync(SqlitePath, SyntheticLibraryOptions.Small, codeMigrations, TestContext.Current.CancellationToken);
        WriteDatabaseConfiguration(null);
    }

    [Fact]
    public async Task Import_AllSteps_MovesTheServerToAVerifiedPostgreSqlDatabase()
    {
        var sqliteHash = await HashAsync(SqlitePath);

        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportPreflight));
        Assert.Equal(ImportStage.Preflighted, (await ReadStateAsync())!.Step);
        using (var resource = typeof(PostgreSqlImportCommand).Assembly.GetManifestResourceStream("Jellyfin.Server.Resources.PostgreSqlImport.jellyfin.load")!)
        using (var copy = new MemoryStream())
        {
            await resource.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(copy.ToArray(), await File.ReadAllBytesAsync(Path.Combine(ImportDirectory, PostgreSqlImportCommand.LoadFileName), TestContext.Current.CancellationToken));
        }

        Assert.Contains("Result: passed", await File.ReadAllTextAsync(Path.Combine(ImportDirectory, "preflight-report.txt"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await AssertStartRefusedAsync("PostgreSqlImportSeed");

        await CreateTargetDatabaseAsync();
        WriteDatabaseConfiguration(_databaseName);
        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportSeed));
        Assert.Equal(ImportStage.Seeded, (await ReadStateAsync())!.Step);
        await AssertStartRefusedAsync("PostgreSqlImportFinalize");

        await LoadAsync();
        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportFinalize));

        Assert.Null(await ReadStateAsync());
        Assert.False(File.Exists(SqlitePath));
        Assert.Equal(sqliteHash, await HashAsync(SqlitePath + DatabaseImportGuard.ImportedSuffix));

        // The server starts on PostgreSQL with nothing left to migrate.
        await DatabaseImportGuard.EnsureNoImportInProgressAsync(_paths.DataPath, ReadDatabaseConfiguration(), TestContext.Current.CancellationToken);
        await using (var connection = await OpenTargetAsync())
        await using (var command = new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"", connection))
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            var applied = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                applied.Add(reader.GetString(0));
            }

            Assert.Superset(JellyfinMigrationService.GetCodeMigrationIds().ToHashSet(StringComparer.Ordinal), applied);
        }

        // Pointing the server back at SQLite would start it on an empty database.
        File.Delete(Path.Combine(_paths.ConfigurationDirectoryPath, "database.xml"));
        await AssertStartRefusedAsync("imported into PostgreSQL");
    }

    [Fact]
    public async Task Preflight_UnimportableValue_FailsWithAReportAndNoState()
    {
        Execute(SqlitePath, "UPDATE BaseItems SET Name = 'a' || char(0) || 'b' WHERE rowid = (SELECT max(rowid) FROM BaseItems)");

        Assert.Equal(ImportExitCode.ChecksFailed, await RunAsync(StartupMode.PostgreSqlImportPreflight));

        Assert.Null(await ReadStateAsync());
        Assert.False(File.Exists(Path.Combine(ImportDirectory, PostgreSqlImportCommand.SnapshotFileName)));
        Assert.Contains("NulInText BaseItems.Name x1", await File.ReadAllTextAsync(Path.Combine(ImportDirectory, "preflight-report.txt"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Steps_OutOfOrderOrOnTheWrongDatabase_AreRefusedWithoutChanges()
    {
        await AssertRefusedAsync(StartupMode.PostgreSqlImportSeed, "No PostgreSQL import is in progress");
        await AssertRefusedAsync(StartupMode.PostgreSqlImportFinalize, "No PostgreSQL import is in progress");

        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportPreflight));
        await AssertRefusedAsync(StartupMode.PostgreSqlImportFinalize, "needs the import to be Seeded");

        // Still configured for SQLite.
        await AssertRefusedAsync(StartupMode.PostgreSqlImportSeed, "configured for 'Jellyfin-SQLite'");

        // The database does not exist; seed never creates it.
        WriteDatabaseConfiguration(_databaseName);
        await AssertRefusedAsync(StartupMode.PostgreSqlImportSeed, "does not exist");

        // Another server version.
        await CreateTargetDatabaseAsync();
        await AssertRefusedAsync(StartupMode.PostgreSqlImportSeed, "started by server 10.12.0.0", new Version(10, 12, 0, 1));

        // Not empty: the provider's startup checks name what is in the way.
        await using (var connection = await OpenTargetAsync())
        {
            await ExecuteAsync(connection, "CREATE TABLE \"Leftover\" (\"Id\" integer)");
        }

        await AssertRefusedAsync(StartupMode.PostgreSqlImportSeed, "table \"Leftover\"");
        Assert.Equal(ImportStage.Preflighted, (await ReadStateAsync())!.Step);
    }

    [Fact]
    public async Task Finalize_DamagedLoad_FailsAndSucceedsAfterLoadingAgain()
    {
        await PreflightAndSeedAsync();
        await LoadAsync();
        await using (var connection = await OpenTargetAsync())
        {
            await ExecuteAsync(connection, "DELETE FROM \"UserData\" WHERE ctid = (SELECT min(ctid) FROM \"UserData\")");
        }

        Assert.Equal(ImportExitCode.ChecksFailed, await RunAsync(StartupMode.PostgreSqlImportFinalize));
        Assert.Equal(ImportStage.Seeded, (await ReadStateAsync())!.Step);
        Assert.True(File.Exists(SqlitePath));
        Assert.Contains("RowCountMismatch UserData", await File.ReadAllTextAsync(Path.Combine(ImportDirectory, "finalize-report.txt"), TestContext.Current.CancellationToken), StringComparison.Ordinal);

        await LoadAsync();
        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportFinalize));
        Assert.Null(await ReadStateAsync());
    }

    [Fact]
    public async Task Finalize_SqliteChangedAfterPreflight_IsRefused()
    {
        await PreflightAndSeedAsync();
        await LoadAsync();
        File.SetLastWriteTimeUtc(SqlitePath, DateTime.UtcNow.AddMinutes(5));

        await AssertRefusedAsync(StartupMode.PostgreSqlImportFinalize, "changed after preflight");
        Assert.Equal(ImportStage.Seeded, (await ReadStateAsync())!.Step);
    }

    [Fact]
    public async Task Abort_BeforeCommit_LeavesSqliteAsItWas()
    {
        var sqliteHash = await HashAsync(SqlitePath);
        await PreflightAndSeedAsync();

        await AssertRefusedAsync(StartupMode.PostgreSqlImportPreflight, "past preflight");
        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportAbort));

        Assert.Null(await ReadStateAsync());
        Assert.False(File.Exists(Path.Combine(ImportDirectory, PostgreSqlImportCommand.SnapshotFileName)));
        Assert.True(File.Exists(Path.Combine(ImportDirectory, "preflight-report.txt")));
        Assert.Equal(sqliteHash, await HashAsync(SqlitePath));
        WriteDatabaseConfiguration(null);
        await DatabaseImportGuard.EnsureNoImportInProgressAsync(_paths.DataPath, ReadDatabaseConfiguration(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Committed_AbortIsRefusedAndFinalizeCompletes()
    {
        await PreflightAndSeedAsync();
        var state = (await ReadStateAsync())!;
        await (state with { Step = ImportStage.Committed }).WriteAsync(_paths.DataPath, TestContext.Current.CancellationToken);

        await AssertRefusedAsync(StartupMode.PostgreSqlImportAbort, "already committed");
        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportFinalize));

        Assert.Null(await ReadStateAsync());
        Assert.True(File.Exists(SqlitePath + DatabaseImportGuard.ImportedSuffix));
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (TestDatabase.PostgreSqlConnectionString is { } serverConnectionString)
        {
            PostgreSqlTestDatabase.ClearAllPools();
            await using var connection = new NpgsqlConnection(serverConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        }

        Directory.Delete(_root, true);
    }

    private static async Task<string> HashAsync(string path)
        => Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static void Execute(string sqlitePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={sqlitePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The statements are constants of this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // The statements are constants of this class.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> RunAsync(StartupMode mode, Version? version = null)
    {
        _ = ServerConnectionString;
        _log.Clear();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new ListLoggerProvider(_log)));
        return await new PostgreSqlImportCommand(_paths, new ConfigurationBuilder().Build(), loggerFactory, version ?? _serverVersion, TimeProvider.System)
            .RunAsync(mode, null, TestContext.Current.CancellationToken);
    }

    private async Task AssertRefusedAsync(StartupMode mode, string reason, Version? version = null)
    {
        Assert.Equal(ImportExitCode.Refused, await RunAsync(mode, version));
        Assert.True(_log.Any(line => line.Contains(reason, StringComparison.Ordinal)), $"Expected '{reason}' in: {string.Join(" | ", _log)}");
    }

    private Task<ImportState?> ReadStateAsync() => ImportState.ReadAsync(_paths.DataPath, TestContext.Current.CancellationToken);

    private async Task AssertStartRefusedAsync(string expectedMessage)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseImportGuard.EnsureNoImportInProgressAsync(_paths.DataPath, ReadDatabaseConfiguration(), TestContext.Current.CancellationToken));
        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
    }

    private DatabaseConfigurationOptions ReadDatabaseConfiguration()
    {
        var path = Path.Combine(_paths.ConfigurationDirectoryPath, "database.xml");
        return File.Exists(path)
            ? (DatabaseConfigurationOptions)new MyXmlSerializer().DeserializeFromFile(typeof(DatabaseConfigurationOptions), path)!
            : new DatabaseConfigurationOptions { DatabaseType = PostgreSqlImportCommand.SqliteDatabaseType };
    }

    private void WriteDatabaseConfiguration(string? postgreSqlDatabase)
    {
        var configuration = postgreSqlDatabase is null
            ? new DatabaseConfigurationOptions { DatabaseType = PostgreSqlImportCommand.SqliteDatabaseType, LockingBehavior = DatabaseLockingBehaviorTypes.NoLock }
            : new DatabaseConfigurationOptions
            {
                DatabaseType = PostgreSqlImportCommand.PostgreSqlDatabaseType,
                LockingBehavior = DatabaseLockingBehaviorTypes.NoLock,
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = string.Empty,
                    PluginAssembly = string.Empty,
                    ConnectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = postgreSqlDatabase }.ConnectionString
                }
            };
        new MyXmlSerializer().SerializeToFile(configuration, Path.Combine(_paths.ConfigurationDirectoryPath, "database.xml"));
    }

    private async Task CreateTargetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(ServerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, $"CREATE DATABASE \"{_databaseName}\" TEMPLATE template0 ENCODING 'UTF8'");
    }

    private async Task<NpgsqlConnection> OpenTargetAsync()
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = _databaseName, Pooling = false }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private async Task PreflightAndSeedAsync()
    {
        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportPreflight));
        await CreateTargetDatabaseAsync();
        WriteDatabaseConfiguration(_databaseName);
        Assert.Equal(ImportExitCode.Success, await RunAsync(StartupMode.PostgreSqlImportSeed));
    }

    private async Task LoadAsync()
    {
        await using var connection = await OpenTargetAsync();
        Assert.SkipUnless(await DataOnlyLoader.CanLoadAsync(connection, TestContext.Current.CancellationToken), "Loading without foreign key checks needs a superuser.");
        await DataOnlyLoader.LoadAsync(Path.Combine(ImportDirectory, PostgreSqlImportCommand.SnapshotFileName), connection, TestContext.Current.CancellationToken);
    }

    private sealed class ListLoggerProvider(List<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(logLevel + ": " + formatter(state, exception) + (exception is null ? string.Empty : " EX " + exception.GetType().Name + ": " + exception.Message));
            }
        }

        public void Dispose()
        {
        }
    }
}

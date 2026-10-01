using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Testing;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

/// <summary>
/// The statistics a library scan leaves behind on PostgreSQL. Unlike SQLite, the refresh does not skip an empty
/// library: a new PostgreSQL database already describes the item table by its placeholder row, and the plans of a
/// library filled after analysing it empty were measured as good as or better than those of one never analysed.
/// </summary>
[Trait("Provider", "PostgreSql")]
public sealed class PostgreSqlDatabaseStatisticsTests : IDisposable
{
    private readonly PostgreSqlTestDatabase? _database;
    private readonly RecordingLogger _logger = new();
    private readonly List<string> _roles = [];
    private readonly IJellyfinDatabaseProvider _provider;

    public PostgreSqlDatabaseStatisticsTests()
    {
        if (TestDatabase.PostgreSqlConnectionString is { } connectionString)
        {
            _database = new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions { ApplicationPaths = Mock.Of<IApplicationPaths>() });
        }

        _provider = new PostgreSqlDatabaseProvider(null!, _logger)
        {
            DbContextFactory = _database?.CreateDbContextFactory()
        };
    }

    private PostgreSqlTestDatabase Database
    {
        get
        {
            Assert.SkipWhen(_database is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
            return _database;
        }
    }

    [Fact]
    public async Task RefreshStatistics_EmptyLibrary_AnalyzesEveryModelTable()
    {
        Assert.False(await HasLibraryItemsAsync());
        Assert.NotEmpty(await ReadUnanalyzedTablesAsync());

        await _provider.RefreshStatistics(TestContext.Current.CancellationToken);

        Assert.Empty(await ReadUnanalyzedTablesAsync());
    }

    [Fact]
    public async Task RefreshStatistics_LibraryChanged_Reanalyzes()
    {
        SeedEpisodes(10);
        await _provider.RefreshStatistics(TestContext.Current.CancellationToken);
        SeedEpisodes(5);

        await _provider.RefreshStatistics(TestContext.Current.CancellationToken);

        Assert.Equal(CountItems(), await ReadAnalyzedItemCountAsync());
    }

    [Fact]
    public async Task RefreshStatistics_WhileAWriteHoldsTheWriteLock_DoesNotWaitForIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var writeLock = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance, TimeSpan.FromMinutes(2));
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>(Database.Options);
        writeLock.Initialise(builder);
        var options = builder.Options;
        JellyfinDbContext CreateContext() => new(options, NullLogger<JellyfinDbContext>.Instance, Database.Provider, writeLock);
        _provider.DbContextFactory = new ContextFactory(CreateContext);

        await using var writer = CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync(cancellationToken);
        writer.BaseItems.Add(Episode());
        await writer.SaveChangesAsync(cancellationToken);

        var refresh = _provider.RefreshStatistics(cancellationToken);
        var first = await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));

        Assert.Same(refresh, first);
        await refresh;
        await transaction.RollbackAsync(cancellationToken);
    }

    [Fact]
    public async Task RefreshStatistics_TablesTheRoleMayNotAnalyze_WarnsAndCarriesOn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = Database;
        var role = "jf_t_stats_" + Guid.NewGuid().ToString("N")[..8];
        var password = "pw-" + Guid.NewGuid().ToString("N");
        await ExecuteOnServerAsync($"CREATE ROLE {role} LOGIN PASSWORD '{password}'");
        _roles.Add(role);

        // Reads everything, owns nothing: only the owner of a table may analyze it.
        await ExecuteAsync($"GRANT SELECT ON ALL TABLES IN SCHEMA public TO {role}");

        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role, Password = password, Pooling = false }.ConnectionString;
        IJellyfinDatabaseProvider provider = new PostgreSqlDatabaseProvider(null!, _logger);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(builder, new DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-PostgreSQL",
            CustomProviderOptions = new CustomDatabaseOptions { PluginName = string.Empty, PluginAssembly = string.Empty, ConnectionString = connectionString }
        });
        var options = builder.Options;
        provider.DbContextFactory = new ContextFactory(() => new JellyfinDbContext(options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)));
        var unanalyzed = await ReadUnanalyzedTablesAsync();
        Assert.NotEmpty(unanalyzed);

        await provider.RefreshStatistics(cancellationToken);

        int tableCount;
        await using (var context = database.CreateDbContext())
        {
            tableCount = context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables.Count(t => t.Name != "__EFMigrationsHistory");
        }

        var warnings = _logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.StartsWith("PostgreSQL did not refresh the statistics", StringComparison.Ordinal)).ToList();
        Assert.Equal(tableCount, warnings.Count);
        Assert.Contains(warnings, e => e.Message.Contains("\"BaseItems\"", StringComparison.Ordinal));
        Assert.Equal(unanalyzed.Order(StringComparer.Ordinal), (await ReadUnanalyzedTablesAsync()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RefreshStatistics_Cancelled_Throws()
    {
        _ = Database;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.RefreshStatistics(cancellation.Token));
    }

    public void Dispose()
    {
        _database?.Dispose();
        if (_roles.Count > 0)
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var role in _roles)
            {
                ExecuteOnServerAsync($"DROP ROLE IF EXISTS \"{role}\"").GetAwaiter().GetResult();
            }
        }
    }

    private static BaseItemEntity Episode() => new()
    {
        Id = Guid.NewGuid(),
        Type = "MediaBrowser.Controller.Entities.TV.Episode",
        IsFolder = false
    };

    private void SeedEpisodes(int count)
    {
        using var context = Database.CreateDbContext();
        context.BaseItems.AddRange(Enumerable.Range(0, count).Select(_ => Episode()));
        context.SaveChanges();
    }

    private long CountItems()
    {
        using var context = Database.CreateDbContext();
        return context.BaseItems.LongCount();
    }

    private async Task<long> ReadAnalyzedItemCountAsync()
        => Convert.ToInt64(await ScalarAsync("SELECT reltuples FROM pg_class WHERE oid = '\"BaseItems\"'::regclass"), CultureInfo.InvariantCulture);

    private async Task<bool> HasLibraryItemsAsync()
    {
        await using var context = Database.CreateDbContext();
        return await context.BaseItems.AnyAsync(e => !e.IsFolder && e.Type != "PLACEHOLDER", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads the model tables that have never been analyzed or vacuumed, which PostgreSQL marks with a negative row count.
    /// </summary>
    private async Task<List<string>> ReadUnanalyzedTablesAsync()
    {
        await using var context = Database.CreateDbContext();
        var tables = context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables
            .Where(t => t.Name != "__EFMigrationsHistory")
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT c.relname FROM pg_class c WHERE c.relnamespace = current_schema()::regnamespace AND c.relkind = 'r' AND c.reltuples < 0";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var name = reader.GetString(0);
            if (tables.Contains(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements are constants.")]
    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements are constants.")]
    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only statements built from a generated role name are executed.")]
    private static async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.PostgreSqlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ContextFactory(Func<JellyfinDbContext> createDbContext) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => createDbContext();
    }

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

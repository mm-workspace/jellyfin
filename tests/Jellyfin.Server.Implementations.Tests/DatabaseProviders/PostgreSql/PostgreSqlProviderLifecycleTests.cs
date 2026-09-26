using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

[Trait("Provider", "PostgreSql")]
public sealed class PostgreSqlProviderLifecycleTests : IDisposable
{
    private readonly PostgreSqlTestDatabase? _database;

    public PostgreSqlProviderLifecycleTests()
    {
        if (TestDatabase.PostgreSqlConnectionString is { } connectionString)
        {
            _database = new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions());
            _database.Provider.DbContextFactory = _database.CreateDbContextFactory();
        }
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
    public async Task RunScheduledOptimisation_AnalyzesEveryModelTable()
    {
        await Database.Provider.RunScheduledOptimisation(TestContext.Current.CancellationToken);

        await using var context = Database.CreateDbContext();
        var tableCount = context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables.Count(t => t.Name != "__EFMigrationsHistory");
        long analyzed = 0;
        for (var attempt = 0; attempt < 50 && analyzed < tableCount; attempt++)
        {
            await context.Database.ExecuteSqlRawAsync("SELECT pg_stat_clear_snapshot()", TestContext.Current.CancellationToken);
            analyzed = await ScalarAsync(context, "SELECT count(*) FROM pg_stat_user_tables WHERE last_analyze IS NOT NULL AND relname <> '__EFMigrationsHistory'");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(tableCount, analyzed);
    }

    [Fact]
    public async Task RunScheduledOptimisation_Cancelled_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Database.Provider.RunScheduledOptimisation(cancellation.Token));
    }

    [Fact]
    public async Task RunShutdownTask_ClosesIdlePooledConnections()
    {
        await using (var context = Database.CreateDbContext())
        {
            await context.Users.CountAsync(TestContext.Current.CancellationToken);
        }

        await Database.Provider.RunShutdownTask(TestContext.Current.CancellationToken);

        await using var admin = new NpgsqlConnection(TestDatabase.PostgreSqlConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
        command.Parameters.AddWithValue("name", Database.DatabaseName);
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PurgeDatabase_RolledBackTransaction_KeepsData()
    {
        await AddUserAsync("keep");

        await using (var context = Database.CreateDbContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await Database.Provider.PurgeDatabase(context, null);
            Assert.Equal(0, await context.Users.CountAsync(TestContext.Current.CancellationToken));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = Database.CreateDbContext())
        {
            Assert.Equal(1, await context.Users.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task PurgeDatabase_Table_AlsoEmptiesTablesReferencingIt()
    {
        var user = await AddUserAsync("purge");
        await using (var context = Database.CreateDbContext())
        {
            var preferences = new DisplayPreferences(user.Id, Guid.NewGuid(), "client");
            preferences.HomeSections.Add(new HomeSection { Order = 0, Type = Jellyfin.Database.Implementations.Enums.HomeSectionType.LatestMedia });
            context.DisplayPreferences.Add(preferences);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = Database.CreateDbContext())
        {
            await Database.Provider.PurgeDatabase(context, [TableName(context, typeof(DisplayPreferences))]);
            Assert.Equal(0, await context.DisplayPreferences.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await context.Set<HomeSection>().CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await context.Users.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task PurgeDatabase_UnknownTable_Throws()
    {
        await using var context = Database.CreateDbContext();

        await Assert.ThrowsAsync<ArgumentException>(() => Database.Provider.PurgeDatabase(context, ["NoSuchTable"]));
    }

    [Fact]
    public async Task CompleteDatabaseRestoreAsync_StaleSequences_NextInsertsFollowTheRestoredIds()
    {
        await using (var context = Database.CreateDbContext())
        {
            for (var i = 0; i < 50; i++)
            {
                context.ActivityLogs.Add(new ActivityLog("name", "type", Guid.NewGuid()));
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Simulate a restore that wrote explicit ids without advancing the sequence.
            await context.Database.ExecuteSqlRawAsync("SELECT setval(pg_get_serial_sequence('\"ActivityLogs\"', 'Id'), 1, false)", TestContext.Current.CancellationToken);
            await Database.Provider.CompleteDatabaseRestoreAsync(context, TestContext.Current.CancellationToken);
        }

        await using (var context = Database.CreateDbContext())
        {
            var log = new ActivityLog("next", "type", Guid.NewGuid());
            context.ActivityLogs.Add(log);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(51, log.Id);
        }
    }

    [Fact]
    public async Task CompleteDatabaseRestoreAsync_CoversEveryIdentityColumn()
    {
        await using var context = Database.CreateDbContext();
        var identityProperties = context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ValueGenerated == ValueGenerated.OnAdd && (p.ClrType == typeof(int) || p.ClrType == typeof(long)) && p.IsPrimaryKey())
            .Select(p => p.DeclaringType.ContainingEntityType.GetTableName())
            .ToHashSet(StringComparer.Ordinal);

        var identityColumns = await ScalarAsync(context, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND is_identity = 'YES'");

        await Database.Provider.CompleteDatabaseRestoreAsync(context, TestContext.Current.CancellationToken);
        Assert.NotEmpty(identityProperties);
        Assert.Equal(identityProperties.Count, identityColumns);
    }

    public void Dispose()
    {
        _database?.Dispose();
    }

    private static string TableName(DbContext context, Type entityType)
        => context.GetService<IDesignTimeModel>().Model.FindEntityType(entityType)!.GetSchemaQualifiedTableName()!;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements are constants.")]
    private static async Task<long> ScalarAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<User> AddUserAsync(string name)
    {
        await using var context = Database.CreateDbContext();
        var user = new User(name, "provider", "reset");
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }
}

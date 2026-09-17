using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.PostgreSQL.ValueConverters;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Configures jellyfin to use a PostgreSQL database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PostgreSQL")]
public sealed class PostgreSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    /// <summary>
    /// The oldest PostgreSQL major version this provider supports.
    /// </summary>
    internal const int MinimumServerVersion = 16;

    /// <summary>
    /// The collation every text column uses, matching SQLite's byte-wise comparison.
    /// </summary>
    internal const string BinaryCollation = "C";

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<PostgreSqlDatabaseProvider> _logger;
    private string? _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">A logger.</param>
    public PostgreSqlDatabaseProvider(IApplicationPaths applicationPaths, ILogger<PostgreSqlDatabaseProvider> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <summary>
    /// Gets the locking behavior Jellyfin runs with on PostgreSQL for a configured locking behavior.
    /// </summary>
    /// <param name="configured">The locking behavior from the database configuration.</param>
    /// <returns>The locking behavior to use.</returns>
    /// <exception cref="InvalidOperationException">The configured locking behavior is not supported on PostgreSQL.</exception>
    public static DatabaseLockingBehaviorTypes GetEffectiveLockingBehavior(DatabaseLockingBehaviorTypes configured)
    {
        // Parts of Jellyfin still rely on SQLite allowing only one writer at a time, so writes stay serialized.
        return configured switch
        {
            DatabaseLockingBehaviorTypes.NoLock or DatabaseLockingBehaviorTypes.SerializedWrites => DatabaseLockingBehaviorTypes.SerializedWrites,
            _ => throw new InvalidOperationException(
                $"The PostgreSQL database provider does not support the {configured} locking behavior. Remove LockingBehavior from database.xml or set it to {nameof(DatabaseLockingBehaviorTypes.SerializedWrites)}.")
        };
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        GetEffectiveLockingBehavior(databaseConfiguration.LockingBehavior);
        var settings = PostgreSqlOptionsReader.Read(databaseConfiguration, _applicationPaths, _logger);
        _connectionString = settings.ConnectionString;
        _logger.LogInformation("PostgreSQL connection: {Connection}", settings.Description);

        options
            .UseNpgsql(
                settings.ConnectionString,
                npgsqlOptions => npgsqlOptions
                    .MigrationsAssembly(GetType().Assembly)
                    .SetPostgresVersion(MinimumServerVersion, 0)
                    .CommandTimeout(settings.CommandTimeout))
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MultipleCollectionIncludeWarning))
            .AddInterceptors(new PostgreSqlStartupCheckInterceptor(
                new PostgreSqlStartupChecks(new NpgsqlConnectionStringBuilder(settings.ConnectionString), _logger),
                settings.ConnectionString));

        if (settings.EnableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging();
            _logger.LogInformation("EnableSensitiveDataLogging is enabled on the PostgreSQL connection");
        }
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Behave like SQLite: text is unbounded and compared byte by byte, and DateTime values are UTC.
        configurationBuilder.Properties<string>()
            .HaveColumnType("text")
            .UseCollation(BinaryCollation);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();

        // PostgreSQL has no unsigned types and Npgsql would map uint to the system type xid.
        configurationBuilder.Properties<uint>().HaveConversion<long>();
    }

    /// <inheritdoc/>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Table names come from the model and are quoted.")]
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        // Autovacuum reclaims space on its own; refreshing the planner statistics is what helps after large changes.
        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var catalog = PostgreSqlModelCatalog.Create(context.GetService<IDesignTimeModel>().Model);
            var sqlGenerationHelper = context.GetService<ISqlGenerationHelper>();
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var table in catalog.Tables.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var command = connection.CreateCommand();
                    await using (command.ConfigureAwait(false))
                    {
                        command.CommandText = "ANALYZE " + sqlGenerationHelper.DelimitIdentifier(table.Name, table.Schema);
                        command.CommandTimeout = 0;
                        try
                        {
                            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
                        {
                            _logger.LogWarning("Cannot analyze {Table}: the database role is not allowed to.", table.SchemaQualifiedName);
                        }
                    }
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        // Only close idle pooled connections; shutting down runs against a deadline.
        try
        {
            if (_connectionString is not null)
            {
                using var connection = new NpgsqlConnection(_connectionString);
                NpgsqlConnection.ClearPool(connection);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close the PostgreSQL connection pool.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        throw new NotSupportedException("The PostgreSQL database provider does not create migration backups. Back up the database with pg_dump before upgrading.");
    }

    /// <inheritdoc/>
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("The PostgreSQL database provider does not create migration backups. Restore the database with pg_restore.");
    }

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        var catalog = PostgreSqlModelCatalog.Create(dbContext.GetService<IDesignTimeModel>().Model);
        var requested = tableNames is null
            ? catalog.Tables.Values.ToArray()
            : tableNames.Select(name => catalog.Tables.TryGetValue(name, out var table)
                ? table
                : throw new ArgumentException($"The table '{name}' is not part of the Jellyfin model.", nameof(tableNames))).ToArray();

        // TRUNCATE refuses tables referenced by tables it does not truncate as well, and CASCADE would silently empty tables nobody asked for.
        var tables = catalog.WithDependents(requested);
        if (tables.Count > requested.Distinct().Count())
        {
            _logger.LogInformation(
                "Purging also empties {Tables} because they reference the purged tables.",
                string.Join(", ", tables.Except(requested).Select(t => t.SchemaQualifiedName)));
        }

        if (tables.Count == 0)
        {
            return;
        }

        var sqlGenerationHelper = dbContext.GetService<ISqlGenerationHelper>();
        var sql = "TRUNCATE TABLE " + string.Join(", ", tables.Select(t => sqlGenerationHelper.DelimitIdentifier(t.Name, t.Schema))) + " RESTART IDENTITY";
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await dbContext.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
            return;
        }

        var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await dbContext.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task CompleteDatabaseRestoreAsync(JellyfinDbContext dbContext, CancellationToken cancellationToken)
    {
        // Restored rows keep their ids, which leaves the identity sequences behind them.
        var catalog = PostgreSqlModelCatalog.Create(dbContext.GetService<IDesignTimeModel>().Model);
        var sqlGenerationHelper = dbContext.GetService<ISqlGenerationHelper>();
        foreach (var identity in catalog.IdentityColumns)
        {
            var table = sqlGenerationHelper.DelimitIdentifier(identity.Table, identity.Schema);
            var column = sqlGenerationHelper.DelimitIdentifier(identity.Column);
            var sql = string.Concat("SELECT setval(pg_get_serial_sequence({0}, {1}), COALESCE(MAX(", column, "), 0) + 1, false) FROM ", table);
            await dbContext.Database.ExecuteSqlRawAsync(sql, [table, identity.Column], cancellationToken).ConfigureAwait(false);
        }
    }
}

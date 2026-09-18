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
using Jellyfin.Database.Providers.PostgreSQL.Query;
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

    private static readonly ConcurrentDictionary<(string ConnectionString, string? PasswordFile, bool DisableJit, int? HashMemoryMegabytes), Lazy<NpgsqlDataSource>> _dataSources = new();

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<PostgreSqlDatabaseProvider> _logger;
    private NpgsqlDataSource? _dataSource;

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

    /// <summary>
    /// Closes the idle connections of every PostgreSQL connection pool in the process, e.g. before a database is dropped.
    /// </summary>
    internal static void ClearAllPools()
    {
        foreach (var dataSource in _dataSources.Values.Where(e => e.IsValueCreated))
        {
            dataSource.Value.Clear();
        }
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        GetEffectiveLockingBehavior(databaseConfiguration.LockingBehavior);
        var settings = PostgreSqlOptionsReader.Read(databaseConfiguration, _applicationPaths, _logger);
        _logger.LogInformation("PostgreSQL connection: {Connection}", settings.Description);

        _dataSource = GetDataSource(settings);
        options
            .UseNpgsql(
                _dataSource,
                npgsqlOptions => npgsqlOptions
                    .MigrationsAssembly(GetType().Assembly)
                    .SetPostgresVersion(MinimumServerVersion, 0)
                    .CommandTimeout(settings.CommandTimeout))
            .ConfigureWarnings(warnings => warnings
                .Ignore(RelationalEventId.MultipleCollectionIncludeWarning)
                // Each connection string has its own data source and EF Core builds services per data source; that is expected, not a leak.
                .Log(CoreEventId.ManyServiceProvidersCreatedWarning))
            .AddInterceptors(new PostgreSqlStartupCheckInterceptor(
                new PostgreSqlStartupChecks(new NpgsqlConnectionStringBuilder(settings.ConnectionString), _logger),
                settings.ConnectionString));

        // Jellyfin's queries aggregate ids, which PostgreSQL cannot do on uuid columns by itself.
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new JellyfinQueryOptionsExtension());

        // Jellyfin's orderings were written against SQLite, which sorts NULL below every other value.
        options.AddInterceptors(NullsSortLowInterceptor.Instance, StringMatchInterceptor.Instance, GroupRepresentativeInterceptor.Instance);

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
            .UseCollation(BinaryCollation)
            .HaveConversion<DatabaseTextConverter>();
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
            _dataSource?.Clear();
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

    private static NpgsqlDataSource GetDataSource(PostgreSqlConnectionSettings settings)
    {
        // Contexts of every service provider in the process share one pool per connection string.
        return _dataSources.GetOrAdd(
            (settings.ConnectionString, settings.PasswordFile, settings.DisableJit, settings.HashMemoryMegabytes),
            static key => new Lazy<NpgsqlDataSource>(() =>
            {
                var builder = new NpgsqlDataSourceBuilder(key.ConnectionString);
                if (key.PasswordFile is { } passwordFile)
                {
                    // Read for every physical connection, so the password is never part of a connection string
                    // and a rotated password is picked up without a restart.
                    builder.UsePasswordProvider(
                        _ => PostgreSqlOptionsReader.ReadPasswordFile(passwordFile),
                        (_, cancellationToken) => PostgreSqlOptionsReader.ReadPasswordFileAsync(passwordFile, cancellationToken));
                }

                if (GetSessionSetup(key.DisableJit, key.HashMemoryMegabytes) is { } sessionSetup)
                {
                    builder.UsePhysicalConnectionInitializer(
                        connection => SetUpSession(connection, sessionSetup),
                        connection => SetUpSessionAsync(connection, sessionSetup));
                }

                return builder.Build();
            })).Value;
    }

    /// <summary>
    /// Gets the statements a connection runs once, when it is opened.
    /// </summary>
    /// <param name="disableJit">Whether to turn JIT compilation off.</param>
    /// <param name="hashMemoryMegabytes">The memory a hash table may use at least, or <c>null</c>.</param>
    /// <returns>The statements, or <c>null</c> when there are none.</returns>
    internal static string? GetSessionSetup(bool disableJit, int? hashMemoryMegabytes)
    {
        List<string> statements = [];
        if (disableJit)
        {
            statements.Add("SET jit = off");
        }

        if (hashMemoryMegabytes is not null)
        {
            // A hash table may use work_mem x hash_mem_multiplier. The multiplier is only ever raised, as far as it takes
            // to reach the wanted memory with the work_mem of this session (in kB), and 1000 is the most PostgreSQL accepts.
            statements.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"SELECT set_config('hash_mem_multiplier', LEAST(1000, GREATEST(current_setting('hash_mem_multiplier')::numeric, ceil({hashMemoryMegabytes.Value} * 1024.0 * 1000 / setting::numeric) / 1000))::text, false) FROM pg_settings WHERE name = 'work_mem'"));
        }

        return statements.Count == 0 ? null : string.Join("; ", statements);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The statements are built from a flag and a validated integer.")]
    private static void SetUpSession(NpgsqlConnection connection, string sessionSetup)
    {
        using var command = new NpgsqlCommand(sessionSetup, connection);
        command.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The statements are built from a flag and a validated integer.")]
    private static async Task SetUpSessionAsync(NpgsqlConnection connection, string sessionSetup)
    {
        var command = new NpgsqlCommand(sessionSetup, connection);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}

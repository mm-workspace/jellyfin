using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

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

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<PostgreSqlDatabaseProvider> _logger;

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

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        var settings = PostgreSqlOptionsReader.Read(databaseConfiguration, _applicationPaths, _logger);
        _logger.LogInformation("PostgreSQL connection: {Connection}", settings.Description);

        options
            .UseNpgsql(
                settings.ConnectionString,
                npgsqlOptions => npgsqlOptions
                    .MigrationsAssembly(GetType().Assembly)
                    .SetPostgresVersion(MinimumServerVersion, 0)
                    .CommandTimeout(settings.CommandTimeout))
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MultipleCollectionIncludeWarning));

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
    }

    /// <inheritdoc/>
    public Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
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
    public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        throw new NotSupportedException("Purging a PostgreSQL database is not supported yet.");
    }
}

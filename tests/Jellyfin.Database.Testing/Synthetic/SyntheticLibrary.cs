using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Database.Testing.Synthetic;

/// <summary>
/// Fills a Jellyfin database with deterministic rows in every table of the model.
/// </summary>
/// <remarks>
/// Rows are written with the provider's EF type mappings, so values are stored in the same form the server stores them.
/// The rows are consistent with the schema (keys, unique indexes, foreign keys) but not with the server's meaning of the data.
/// </remarks>
public static class SyntheticLibrary
{
    /// <summary>
    /// Creates a SQLite database file with the schema of this build and fills it.
    /// </summary>
    /// <param name="path">The path of the database file to create.</param>
    /// <param name="options">The size and content.</param>
    /// <param name="additionalHistoryRows">Rows to add to the migration history, such as the ids of the code migrations.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the creation.</returns>
    public static async Task CreateSqliteDatabaseAsync(
        string path,
        SyntheticLibraryOptions options,
        IEnumerable<KeyValuePair<string, string>> additionalHistoryRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (File.Exists(path))
        {
            throw new IOException($"The database '{path}' already exists.");
        }

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
                Options = { new CustomDatabaseOption { Key = "path", Value = path } }
            }
        });

        var context = new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        await using (context.ConfigureAwait(false))
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            var history = context.GetService<IHistoryRepository>();
            foreach (var (id, productVersion) in additionalHistoryRows)
            {
#pragma warning disable EF1002 // The script comes from the history repository.
                await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, productVersion)), cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
            }

            await PopulateAsync(context, options, cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Adds rows to every table of a migrated database.
    /// </summary>
    /// <param name="context">A context of the database.</param>
    /// <param name="options">The size and content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of rows written, by table.</returns>
    public static async Task<IReadOnlyDictionary<string, int>> PopulateAsync(JellyfinDbContext context, SyntheticLibraryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var sql = context.GetService<ISqlGenerationHelper>();
        var roundTimestamps = !context.Database.IsSqlite();
        var keys = new SyntheticKeys();
        var written = new Dictionary<string, int>(StringComparer.Ordinal);

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = context.Database.GetDbConnection();
            foreach (var table in SortByDependencies(model.Tables.Where(t => t.Name != HistoryRepository.DefaultTableName)))
            {
                // One value source per table, so adding a table or column elsewhere leaves the other tables' rows unchanged.
                // The PostgreSQL expression indexes apply to every source, since a SQLite source must be importable.
                var writer = new SyntheticTableWriter(
                    table,
                    new SyntheticValues(HashCode(options.Seed, table.Name)),
                    keys,
                    roundTimestamps,
                    PostgreSqlBaselineSql.ExpressionIndexes.Where(i => i.Table == table.Name).SelectMany(i => i.Columns));
                var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await writer.ReadExistingRowsAsync(connection, transaction, sql, cancellationToken).ConfigureAwait(false);
                    written[table.Name] = await writer.WriteAsync(
                        connection,
                        transaction,
                        sql,
                        RowCount(table.Name, options),
                        options.EdgeValues ? SyntheticValues.EdgeRowCount : 0,
                        cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return written;
    }

    private static int RowCount(string table, SyntheticLibraryOptions options)
    {
        var items = options.Items;
        var users = options.Users;
        return table switch
        {
            "Users" => users,
            "BaseItems" => items,
            "AccessSchedules" => users,
            "ActivityLogs" => items / 10,
            "AncestorIds" => items * 2,
            "ApiKeys" => 3,
            "AttachmentStreamInfos" => items / 20,
            "BaseItemImageInfos" => items * 2,
            "BaseItemMetadataFields" => items / 20,
            "BaseItemProviders" => items * 2,
            "BaseItemTrailerTypes" => items / 50,
            "Chapters" => items,
            "CustomItemDisplayPreferences" => users * 5,
            "DeviceOptions" => users * 2,
            "Devices" => users * 2,
            "DisplayPreferences" => users * 3,
            "HomeSection" => users * 6,
            "ImageInfos" => users,
            "ItemDisplayPreferences" => users * 5,
            "ItemValues" => items / 10,
            "ItemValuesMap" => items * 3,
            "KeyframeData" => items / 50,
            "LinkedChildren" => items / 10,
            "MediaSegments" => items / 10,
            "MediaStreamInfos" => items * 2,
            "PeopleBaseItemMap" => items * 2,
            "Peoples" => items / 4,
            "Permissions" => users * 10,
            "Preferences" => users * 10,
            "TrickplayInfos" => items / 20,
            "UserData" => items * users / 3,
            _ => Math.Max(1, items / 10)
        };
    }

    private static IEnumerable<ITable> SortByDependencies(IEnumerable<ITable> tables)
    {
        var remaining = tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(t => t.ForeignKeyConstraints.All(f => f.PrincipalTable == t || done.Contains(f.PrincipalTable.Name)))
                ?? throw new InvalidOperationException("The foreign keys of the model form a cycle.");
            remaining.Remove(next);
            done.Add(next.Name);
            yield return next;
        }
    }

    private static int HashCode(int seed, string text)
    {
        // FNV-1a: string.GetHashCode changes between processes.
        var hash = 2166136261u ^ (uint)seed;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)hash;
    }
}

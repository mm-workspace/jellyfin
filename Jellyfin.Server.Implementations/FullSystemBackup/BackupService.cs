using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.Implementations.StorageHelpers;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Contains methods for creating and restoring backups.
/// </summary>
public class BackupService : IBackupService
{
    private const string ManifestEntryName = "manifest.json";

    /// <summary>
    /// The database configuration belongs to the installation it was written for; restoring it elsewhere would point that server at this server's database.
    /// </summary>
    private const string DatabaseConfigurationFileName = "database.xml";

    /// <summary>
    /// The number of rows in a row that may fail to read before a table is considered unreadable.
    /// </summary>
    internal const int MaxConsecutiveReadFailures = 1000;

    /// <summary>
    /// Reads every row of one table of the model. The entity type is only known at runtime, so this is bound
    /// through <see cref="_readTableRowsMethod"/>.
    /// </summary>
    private static readonly MethodInfo _readTableRowsMethod = typeof(BackupService).GetMethod(nameof(ReadTableRows), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Counts the rows of one table of the model, bound through reflection like <see cref="_readTableRowsMethod"/>.
    /// </summary>
    private static readonly MethodInfo _countTableRowsMethod = typeof(BackupService).GetMethod(nameof(CountTableRowsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly ILogger<BackupService> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly IJellyfinDatabaseProvider _jellyfinDatabaseProvider;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILibraryManager _libraryManager;
    private static readonly JsonSerializerOptions _serializerSettings = new JsonSerializerOptions(JsonSerializerDefaults.General)
    {
        AllowTrailingCommas = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    private readonly Version _backupEngineVersion = new Version(0, 2, 0);

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupService"/> class.
    /// </summary>
    /// <param name="logger">A logger.</param>
    /// <param name="dbProvider">A Database Factory.</param>
    /// <param name="applicationHost">The Application host.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="jellyfinDatabaseProvider">The Jellyfin database Provider in use.</param>
    /// <param name="applicationLifetime">The SystemManager.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public BackupService(
        ILogger<BackupService> logger,
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IServerApplicationHost applicationHost,
        IServerApplicationPaths applicationPaths,
        IJellyfinDatabaseProvider jellyfinDatabaseProvider,
        IHostApplicationLifetime applicationLifetime,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _dbProvider = dbProvider;
        _applicationHost = applicationHost;
        _applicationPaths = applicationPaths;
        _jellyfinDatabaseProvider = jellyfinDatabaseProvider;
        _hostApplicationLifetime = applicationLifetime;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets or sets the number of rows a restore writes before it saves them and forgets them again. Tests lower it
    /// to exercise the ordering between batches.
    /// </summary>
    internal int RestoreBatchSize { get; set; } = 5000;

    /// <inheritdoc/>
    public void ScheduleRestoreAndRestartServer(string archivePath)
    {
        _applicationHost.RestoreBackupPath = archivePath;
        _applicationHost.ShouldRestart = true;
        _applicationHost.NotifyPendingRestart();
        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            _hostApplicationLifetime.StopApplication();
        });
    }

    /// <inheritdoc/>
    public async Task RestoreBackupAsync(string archivePath)
    {
        _logger.LogWarning("Begin restoring system to {BackupArchive}", archivePath); // Info isn't cutting it
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Requested backup file '{archivePath}' does not exist.");
        }

        StorageHelper.TestCommonPathsForStorageCapacity(_applicationPaths, _logger);

        var fileStream = File.OpenRead(archivePath);
        await using (fileStream.ConfigureAwait(false))
        {
            using var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Read, false);
            var zipArchiveEntry = zipArchive.GetEntry(ManifestEntryName);

            if (zipArchiveEntry is null)
            {
                throw new NotSupportedException($"The loaded archive '{archivePath}' does not appear to be a Jellyfin backup as its missing the '{ManifestEntryName}'.");
            }

            BackupManifest? manifest;
            var manifestStream = await zipArchiveEntry.OpenAsync().ConfigureAwait(false);
            await using (manifestStream.ConfigureAwait(false))
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, _serializerSettings, CancellationToken.None).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Cannot restore backup with an empty manifest.");
            }

            if (manifest.ServerVersion > _applicationHost.ApplicationVersion) // newer versions of Jellyfin should be able to load older versions as we have migrations.
            {
                throw new NotSupportedException($"The loaded archive '{archivePath}' is made for a newer version of Jellyfin ({manifest.ServerVersion}) and cannot be loaded in this version.");
            }

            if (!TestBackupVersionCompatibility(manifest.BackupEngineVersion))
            {
                throw new NotSupportedException($"The loaded archive '{archivePath}' is made for a newer version of Jellyfin ({manifest.ServerVersion}) and cannot be loaded in this version.");
            }

            void CopyDirectory(string source, string target, string[]? exclude = null)
            {
                var fullSourcePath = NormalizePathSeparator(Path.GetFullPath(source) + Path.DirectorySeparatorChar);
                var fullTargetRoot = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
                var excludePaths = exclude?.Select(e => $"{source}/{e}/").ToArray();
                foreach (var item in zipArchive.Entries)
                {
                    var sourcePath = NormalizePathSeparator(Path.GetFullPath(item.FullName));
                    var targetPath = Path.GetFullPath(Path.Combine(target, Path.GetRelativePath(source, item.FullName)));

                    if (excludePaths is not null && excludePaths.Any(e => item.FullName.StartsWith(e, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    if (source == "Config" && string.Equals(item.FullName, $"Config/{DatabaseConfigurationFileName}", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Keeping the existing {File}; the database configuration in the archive is not restored", DatabaseConfigurationFileName);
                        continue;
                    }

                    if (!sourcePath.StartsWith(fullSourcePath, StringComparison.Ordinal)
                        || !targetPath.StartsWith(fullTargetRoot, StringComparison.Ordinal)
                        || Path.EndsInDirectorySeparator(item.FullName))
                    {
                        continue;
                    }

                    _logger.LogInformation("Restore and override {File}", targetPath);

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    item.ExtractToFile(targetPath, overwrite: true);
                }
            }

            void RestoreFiles()
            {
                CopyDirectory("Config", _applicationPaths.ConfigurationDirectoryPath);
                CopyDirectory("Data", _applicationPaths.DataPath, exclude: ["metadata", "metadata-default"]);
                CopyDirectory("Root", _applicationPaths.RootFolderPath);
                CopyDirectory("Data/metadata", _applicationPaths.InternalMetadataPath);
                CopyDirectory("Data/metadata-default", _applicationPaths.DefaultInternalMetadataPath);
            }

            if (manifest.Options.Database)
            {
                _logger.LogInformation("Begin restoring Database");
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
                    var entityTypes = GetBackupEntityTypes(dbContext);
                    ValidateDatabaseEntries(zipArchive, manifest, entityTypes.Select(e => e.SourceName));

                    var historyEntry = zipArchive.GetEntry($"Database/{nameof(HistoryRow)}.json")!;
                    HistoryRow[] historyEntries;
                    var historyArchive = await historyEntry.OpenAsync().ConfigureAwait(false);
                    await using (historyArchive.ConfigureAwait(false))
                    {
                        historyEntries = await JsonSerializer.DeserializeAsync<HistoryRow[]>(historyArchive, cancellationToken: CancellationToken.None).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Cannot restore backup that has no History data.");
                    }

                    // Keep one connection open for the whole restore, so the transaction runs on the connection the
                    // provider prepares.
                    await dbContext.Database.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        await _jellyfinDatabaseProvider.BeginDatabaseRestoreAsync(dbContext, CancellationToken.None).ConfigureAwait(false);
                        var transaction = await dbContext.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                        await using (transaction.ConfigureAwait(false))
                        {
                            await RestoreHistoryAsync(dbContext, historyEntries).ConfigureAwait(false);

                            _logger.LogInformation("Begin purging database");
                            await _jellyfinDatabaseProvider.PurgeDatabase(dbContext, entityTypes.Select(e => e.SourceName)).ConfigureAwait(false);
                            _logger.LogInformation("Database Purged");

                            // The rows are read and written inside the transaction, so that the archive is streamed
                            // into the database rather than held in memory until the purge has run.
                            var restored = await RestoreTablesAsync(dbContext, zipArchive, entityTypes).ConfigureAwait(false);
                            await VerifyRestoredTablesAsync(dbContext, manifest, entityTypes, restored).ConfigureAwait(false);
                            await _jellyfinDatabaseProvider.CompleteDatabaseRestoreAsync(dbContext, CancellationToken.None).ConfigureAwait(false);
                            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                            _logger.LogInformation("Restored database");

                            // The files are restored only once the database is, so that a failed database restore leaves them
                            // as they were. This runs before the restore is ended on the connection, so that a failure there
                            // cannot skip them.
                            try
                            {
                                RestoreFiles();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogCritical(ex, "The database was restored from {BackupArchive}, but the files were not restored or only partly restored. Restore the backup again", archivePath);
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            await _jellyfinDatabaseProvider.EndDatabaseRestoreAsync(dbContext, CancellationToken.None).ConfigureAwait(false);
                        }
                        finally
                        {
                            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
            else
            {
                RestoreFiles();
            }

            _logger.LogInformation("Restored Jellyfin system from {Date}", manifest.DateCreated);
        }
    }

    /// <summary>
    /// The tables a backup holds, ordered so that a table comes after the tables it references.
    /// </summary>
    /// <remarks>
    /// The set comes from the model rather than from the context's properties, so that a table added to the model is
    /// backed up whether or not it also gets a <see cref="DbSet{TEntity}"/>. Owned types are part of their owner's
    /// table, and a type without a key or a table of its own has no rows to back up.
    /// </remarks>
    /// <param name="dbContext">A context of the database.</param>
    /// <returns>The tables, principals first.</returns>
    private static List<(IEntityType EntityType, string SourceName)> GetBackupEntityTypes(JellyfinDbContext dbContext)
    {
        var remaining = dbContext.Model.GetEntityTypes()
            .Where(e => e.BaseType is null && !e.IsOwned() && !e.IsPropertyBag && e.FindPrimaryKey() is not null && e.GetSchemaQualifiedTableName() is not null)
            .Select(e => (EntityType: e, SourceName: e.GetSchemaQualifiedTableName()!))
            .OrderBy(e => e.SourceName, StringComparer.Ordinal)
            .ToList();
        var known = remaining.Select(e => e.EntityType).ToHashSet();

        var sorted = new List<(IEntityType EntityType, string SourceName)>(remaining.Count);
        var written = new HashSet<IEntityType>();
        while (remaining.Count > 0)
        {
            // A table referencing itself is ordered row by row while it is restored, not here. Tables referencing
            // each other cannot be ordered at all; they keep their name order and the database reports what it
            // rejects.
            var next = remaining.FindIndex(e => e.EntityType.GetForeignKeys().All(
                foreignKey => foreignKey.PrincipalEntityType.Equals(e.EntityType)
                    || written.Contains(foreignKey.PrincipalEntityType)
                    || !known.Contains(foreignKey.PrincipalEntityType)));
            if (next < 0)
            {
                next = 0;
            }

            written.Add(remaining[next].EntityType);
            sorted.Add(remaining[next]);
            remaining.RemoveAt(next);
        }

        return sorted;
    }

    /// <summary>
    /// Replaces the migration history with the one the restored rows belong to, keeping the rows that describe the
    /// schema.
    /// </summary>
    /// <remarks>
    /// A restore replaces the rows of the tables but not the tables themselves, so the migrations of this build that
    /// created them stay applied. Everything else - the routines that changed data, and the ids of migrations this
    /// build no longer knows - describes the data and follows it out of the archive.
    /// </remarks>
    /// <param name="dbContext">The context the restore transaction runs on.</param>
    /// <param name="historyEntries">The history rows the archive holds.</param>
    /// <returns>A task representing the operation.</returns>
    private static async Task RestoreHistoryAsync(JellyfinDbContext dbContext, IEnumerable<HistoryRow> historyEntries)
    {
        var historyRepository = dbContext.GetService<IHistoryRepository>();
        var schemaMigrations = dbContext.GetService<IMigrationsAssembly>().Migrations;
        await historyRepository.CreateIfNotExistsAsync().ConfigureAwait(false);
        foreach (var item in await historyRepository.GetAppliedMigrationsAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (!schemaMigrations.ContainsKey(item.MigrationId))
            {
                await dbContext.Database.ExecuteSqlRawAsync(historyRepository.GetDeleteScript(item.MigrationId), CancellationToken.None).ConfigureAwait(false);
            }
        }

        foreach (var item in historyEntries)
        {
            if (!schemaMigrations.ContainsKey(item.MigrationId))
            {
                await dbContext.Database.ExecuteSqlRawAsync(historyRepository.GetInsertScript(item), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes the rows the archive holds for every table of the model.
    /// </summary>
    /// <param name="dbContext">The context the restore transaction runs on.</param>
    /// <param name="zipArchive">The archive.</param>
    /// <param name="entityTypes">The tables, principals first.</param>
    /// <returns>The number of rows the archive held, by table, leaving out the tables it did not contain.</returns>
    private async Task<Dictionary<string, long>> RestoreTablesAsync(
        JellyfinDbContext dbContext,
        ZipArchive zipArchive,
        IEnumerable<(IEntityType EntityType, string SourceName)> entityTypes)
    {
        var restored = new Dictionary<string, long>(StringComparer.Ordinal);
        var restoreSerializerSettings = CreateRestoreSerializerSettings(dbContext);
        foreach (var (entityType, sourceName) in entityTypes)
        {
            var zipEntry = zipArchive.GetEntry($"Database/{sourceName}.json");
            if (zipEntry is null)
            {
                // Tables added since this backup was created have no rows to import.
                continue;
            }

            _logger.LogInformation("Restore backup of {Table}", sourceName);
            var writer = new RestoreTableWriter(dbContext, entityType, RestoreBatchSize);
            var zipEntryStream = await zipEntry.OpenAsync().ConfigureAwait(false);
            await using (zipEntryStream.ConfigureAwait(false))
            {
                await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonObject>(zipEntryStream, _serializerSettings).ConfigureAwait(false))
                {
                    var entity = item?.Deserialize(entityType.ClrType, restoreSerializerSettings);
                    if (entity is null)
                    {
                        throw new InvalidOperationException($"Cannot deserialize entity '{item}'");
                    }

                    await writer.AddAsync(entity, CancellationToken.None).ConfigureAwait(false);
                }
            }

            var rows = await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            restored[sourceName] = rows;
            _logger.LogInformation("Restored {Number} entries for {Table}", rows, sourceName);
        }

        return restored;
    }

    /// <summary>
    /// Checks that every table the archive contained now holds the number of rows the backup recorded for it.
    /// </summary>
    /// <remarks>
    /// Throwing here rolls the restore back. Backups made before the row counts were recorded, and tables an older
    /// archive does not contain at all, are not checked.
    /// </remarks>
    /// <param name="dbContext">The context the restore transaction runs on.</param>
    /// <param name="manifest">The manifest of the archive.</param>
    /// <param name="entityTypes">The tables of the model.</param>
    /// <param name="restored">The number of rows read per table.</param>
    /// <returns>A task representing the operation.</returns>
    private static async Task VerifyRestoredTablesAsync(
        JellyfinDbContext dbContext,
        BackupManifest manifest,
        IEnumerable<(IEntityType EntityType, string SourceName)> entityTypes,
        IReadOnlyDictionary<string, long> restored)
    {
        if (manifest.TableRowCounts is null)
        {
            return;
        }

        foreach (var (entityType, sourceName) in entityTypes)
        {
            if (!restored.ContainsKey(sourceName) || !manifest.TableRowCounts.TryGetValue(sourceName, out var expected))
            {
                continue;
            }

            var actual = await CountTableAsync(dbContext, entityType).ConfigureAwait(false);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"Cannot restore the database, the table '{sourceName}' holds {actual} rows after the restore but the backup recorded {expected}.");
            }
        }
    }

    private static IAsyncEnumerable<object> ReadTable(JellyfinDbContext dbContext, IEntityType entityType)
        => (IAsyncEnumerable<object>)_readTableRowsMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [dbContext])!;

    private static Task<long> CountTableAsync(JellyfinDbContext dbContext, IEntityType entityType)
        => (Task<long>)_countTableRowsMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [dbContext])!;

    private static IAsyncEnumerable<object> ReadTableRows<TEntity>(JellyfinDbContext dbContext)
        where TEntity : class
        => dbContext.Set<TEntity>().AsAsyncEnumerable();

    private static Task<long> CountTableRowsAsync<TEntity>(JellyfinDbContext dbContext)
        where TEntity : class
        => dbContext.Set<TEntity>().LongCountAsync();

    private static JsonSerializerOptions CreateRestoreSerializerSettings(JellyfinDbContext dbContext)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            var entityType = dbContext.Model.FindEntityType(typeInfo.Type);
            if (entityType is null)
            {
                return;
            }

            foreach (var property in typeInfo.Properties)
            {
                var mappedProperty = entityType.FindProperty(property.Name)?.PropertyInfo;
                if (property.Set is null && mappedProperty?.SetMethod is not null)
                {
                    property.Set = mappedProperty.SetValue;
                }
            }
        });

        return new JsonSerializerOptions(_serializerSettings) { TypeInfoResolver = resolver };
    }

    private static void ValidateDatabaseEntries(ZipArchive archive, BackupManifest manifest, IEnumerable<string> tableNames)
    {
        if (manifest.DatabaseTables is null || manifest.DatabaseTables.Length == 0)
        {
            throw new InvalidOperationException("Cannot restore backup with no database table manifest.");
        }

        var entries = archive.Entries
            .Where(e => e.FullName.StartsWith("Database/", StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.Ordinal))
            .Select(e => e.FullName["Database/".Length..^".json".Length])
            .ToHashSet(StringComparer.Ordinal);
        var knownTables = tableNames.Append(nameof(HistoryRow)).ToHashSet(StringComparer.Ordinal);
        var legacyManifest = manifest.DatabaseTables.All(e => e == typeof(DbSet<>).Name || e == nameof(HistoryRow));

        // Older 0.2 archives recorded DbSet`1 instead of table names. Their table count still
        // identifies a missing entry, without requiring tables introduced by later versions.
        var expectedTables = legacyManifest ? entries : manifest.DatabaseTables.ToHashSet(StringComparer.Ordinal);
        if (entries.Count != manifest.DatabaseTables.Length
            || !entries.SetEquals(expectedTables)
            || !entries.Contains(nameof(HistoryRow))
            || !entries.IsSubsetOf(knownTables)
            || archive.Entries.Count(e => e.FullName.StartsWith("Database/", StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.Ordinal)) != entries.Count)
        {
            throw new InvalidOperationException("Cannot restore backup with missing, duplicate or unsupported database table entries.");
        }
    }

    private bool TestBackupVersionCompatibility(Version backupEngineVersion)
    {
        if (backupEngineVersion == _backupEngineVersion)
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<BackupManifestDto> CreateBackupAsync(BackupOptionsDto backupOptions)
    {
        // Creating a backup runs a database optimization and reads the entire database under a transaction, both of
        // which heavily contend with an active library scan and could capture an inconsistent database state.
        if (_libraryManager.IsScanRunning)
        {
            _logger.LogWarning("Cannot create a backup while a library scan is running.");
            throw new InvalidOperationException("Cannot create a backup while a library scan is running. Please try again once the scan has finished.");
        }

        var manifest = new BackupManifest()
        {
            DateCreated = DateTime.UtcNow,
            ServerVersion = _applicationHost.ApplicationVersion,
            DatabaseTables = null!,
            BackupEngineVersion = _backupEngineVersion,
            Options = Map(backupOptions)
        };

        _logger.LogInformation("Running database optimization before backup");

        try
        {
            await _jellyfinDatabaseProvider.RunScheduledOptimisation(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The optimization only speeds up the database; the backup does not depend on it.
            _logger.LogWarning(ex, "Database optimization before backup failed, continuing with the backup");
        }

        var backupFolder = Path.Combine(_applicationPaths.BackupPath);

        if (!Directory.Exists(backupFolder))
        {
            Directory.CreateDirectory(backupFolder);
        }

        var backupStorageSpace = StorageHelper.GetFreeSpaceOf(_applicationPaths.BackupPath);

        const long FiveGigabyte = 5_368_709_115;
        if (backupStorageSpace.FreeSpace < FiveGigabyte)
        {
            throw new InvalidOperationException($"The backup directory '{backupStorageSpace.Path}' does not have at least '{StorageHelper.HumanizeStorageSize(FiveGigabyte)}' free space. Cannot create backup.");
        }

        var backupPath = Path.Combine(backupFolder, $"jellyfin-backup-{manifest.DateCreated.ToLocalTime():yyyyMMddHHmmss}.zip");

        try
        {
            _logger.LogInformation("Attempting to create a new backup at {BackupPath}", backupPath);
            var fileStream = File.OpenWrite(backupPath);
            await using (fileStream.ConfigureAwait(false))
            using (var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Create, false))
            {
                _logger.LogInformation("Starting backup process");
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
                    dbContext.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    // include the migration history as well
                    var historyRepository = dbContext.GetService<IHistoryRepository>();
                    var migrations = await historyRepository.GetAppliedMigrationsAsync().ConfigureAwait(false);

                    ICollection<(string SourceName, Func<IAsyncEnumerable<object>> ValueFactory)> entityTypes =
                    [
                        .. GetBackupEntityTypes(dbContext)
                            .Select(e => (e.SourceName, ValueFactory: new Func<IAsyncEnumerable<object>>(() => ReadTable(dbContext, e.EntityType)))),
                        (SourceName: nameof(HistoryRow), ValueFactory: () => migrations.ToAsyncEnumerable())
                    ];
                    manifest.DatabaseTables = entityTypes.Select(e => e.SourceName).ToArray();
                    manifest.DatabaseProvider = _jellyfinDatabaseProvider.GetType().GetCustomAttribute<JellyfinDatabaseProviderKeyAttribute>()?.DatabaseProviderKey;
                    manifest.TableRowCounts = new Dictionary<string, long>(StringComparer.Ordinal);

                    // Every table is read from the same snapshot, so rows that reference each other stay consistent.
                    var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead).ConfigureAwait(false);

                    await using (transaction.ConfigureAwait(false))
                    {
                        _logger.LogInformation("Begin Database backup");

                        foreach (var entityType in entityTypes)
                        {
                            _logger.LogInformation("Begin backup of entity {Table}", entityType.SourceName);
                            var zipEntry = zipArchive.CreateEntry(NormalizePathSeparator(Path.Combine("Database", $"{entityType.SourceName}.json")));
                            var entities = 0;
                            var zipEntryStream = await zipEntry.OpenAsync().ConfigureAwait(false);
                            await using (zipEntryStream.ConfigureAwait(false))
                            {
                                var jsonSerializer = new Utf8JsonWriter(zipEntryStream);
                                await using (jsonSerializer.ConfigureAwait(false))
                                {
                                    jsonSerializer.WriteStartArray();

                                    var set = entityType.ValueFactory().ConfigureAwait(false);
                                    var enumerator = set.GetAsyncEnumerator();
                                    await using (enumerator)
                                    {
                                        var consecutiveReadFailures = 0;
                                        while (true)
                                        {
                                            bool hasNext;
                                            try
                                            {
                                                hasNext = await enumerator.MoveNextAsync();
                                                consecutiveReadFailures = 0;
                                            }
                                            catch (Exception ex)
                                            {
                                                // A reader that fails on every row, e.g. after the connection or transaction broke, would otherwise never finish.
                                                if (++consecutiveReadFailures >= MaxConsecutiveReadFailures)
                                                {
                                                    throw new InvalidOperationException($"Could not read the table {entityType.SourceName}: {MaxConsecutiveReadFailures} rows in a row failed to load.", ex);
                                                }

                                                _logger.LogError(ex, "Could not read next entity of type {Table}, the underlying data appears to be corrupt. Skipping this row and continuing backup; the affected database row should be inspected and fixed manually", entityType.SourceName);
                                                continue;
                                            }

                                            if (!hasNext)
                                            {
                                                break;
                                            }

                                            var item = enumerator.Current;
                                            entities++;
                                            try
                                            {
                                                using var document = JsonSerializer.SerializeToDocument(item, _serializerSettings);
                                                document.WriteTo(jsonSerializer);
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogError(ex, "Could not load entity {Entity}", item);
                                                throw;
                                            }
                                        }
                                    }

                                    jsonSerializer.WriteEndArray();
                                }
                            }

                            manifest.TableRowCounts[entityType.SourceName] = entities;
                            _logger.LogInformation("Backup of entity {Table} with {Number} created", entityType.SourceName, entities);
                        }
                    }
                }

                _logger.LogInformation("Backup of folder {Table}", _applicationPaths.ConfigurationDirectoryPath);
                foreach (var item in Directory.EnumerateFiles(_applicationPaths.ConfigurationDirectoryPath, "*.xml", SearchOption.TopDirectoryOnly)
                             .Union(Directory.EnumerateFiles(_applicationPaths.ConfigurationDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)))
                {
                    if (string.Equals(Path.GetFileName(item), DatabaseConfigurationFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    await zipArchive.CreateEntryFromFileAsync(item, NormalizePathSeparator(Path.Combine("Config", Path.GetFileName(item)))).ConfigureAwait(false);
                }

                void CopyDirectory(string source, string target, string filter = "*")
                {
                    if (!Directory.Exists(source))
                    {
                        return;
                    }

                    _logger.LogInformation("Backup of folder {Table}", source);

                    foreach (var item in Directory.EnumerateFiles(source, filter, SearchOption.AllDirectories))
                    {
                        // TODO: @bond make async
                        zipArchive.CreateEntryFromFile(item, NormalizePathSeparator(Path.Combine(target, Path.GetRelativePath(source, item))));
                    }
                }

                CopyDirectory(Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "users"), Path.Combine("Config", "users"));
                CopyDirectory(Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "ScheduledTasks"), Path.Combine("Config", "ScheduledTasks"));
                CopyDirectory(Path.Combine(_applicationPaths.RootFolderPath), "Root");
                CopyDirectory(Path.Combine(_applicationPaths.DataPath, "collections"), Path.Combine("Data", "collections"));
                CopyDirectory(Path.Combine(_applicationPaths.DataPath, "playlists"), Path.Combine("Data", "playlists"));
                CopyDirectory(Path.Combine(_applicationPaths.DataPath, "ScheduledTasks"), Path.Combine("Data", "ScheduledTasks"));
                if (backupOptions.Subtitles)
                {
                    CopyDirectory(Path.Combine(_applicationPaths.DataPath, "subtitles"), Path.Combine("Data", "subtitles"));
                }

                if (backupOptions.Trickplay)
                {
                    CopyDirectory(Path.Combine(_applicationPaths.DataPath, "trickplay"), Path.Combine("Data", "trickplay"));
                }

                if (backupOptions.Metadata)
                {
                    CopyDirectory(Path.Combine(_applicationPaths.InternalMetadataPath), Path.Combine("Data", "metadata"));

                    // If a custom metadata path is configured, the default location may still contain data.
                    if (!string.Equals(
                            Path.GetFullPath(_applicationPaths.DefaultInternalMetadataPath),
                            Path.GetFullPath(_applicationPaths.InternalMetadataPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        CopyDirectory(Path.Combine(_applicationPaths.DefaultInternalMetadataPath), Path.Combine("Data", "metadata-default"));
                    }
                }

                var manifestStream = await zipArchive.CreateEntry(ManifestEntryName).OpenAsync().ConfigureAwait(false);
                await using (manifestStream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest).ConfigureAwait(false);
                }
            }

            _logger.LogInformation("Backup created");
            return Map(manifest, backupPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup, removing {BackupPath}", backupPath);
            try
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            catch (Exception innerEx)
            {
                _logger.LogWarning(innerEx, "Unable to remove failed backup");
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<BackupManifestDto?> GetBackupManifest(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            return null;
        }

        BackupManifest? manifest;
        try
        {
            manifest = await GetManifest(archivePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tried to load manifest from archive {Path} but failed", archivePath);
            return null;
        }

        if (manifest is null)
        {
            return null;
        }

        return Map(manifest, archivePath);
    }

    /// <inheritdoc/>
    public async Task<BackupManifestDto[]> EnumerateBackups()
    {
        if (!Directory.Exists(_applicationPaths.BackupPath))
        {
            return [];
        }

        var archives = Directory.EnumerateFiles(_applicationPaths.BackupPath, "*.zip");
        var manifests = new List<BackupManifestDto>();
        foreach (var item in archives)
        {
            try
            {
                var manifest = await GetManifest(item).ConfigureAwait(false);

                if (manifest is null)
                {
                    continue;
                }

                manifests.Add(Map(manifest, item));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tried to load manifest from archive {Path} but failed", item);
            }
        }

        return manifests.ToArray();
    }

    private static async ValueTask<BackupManifest?> GetManifest(string archivePath)
    {
        var archiveStream = File.OpenRead(archivePath);
        await using (archiveStream.ConfigureAwait(false))
        {
            using var zipStream = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            var manifestEntry = zipStream.GetEntry(ManifestEntryName);
            if (manifestEntry is null)
            {
                return null;
            }

            var manifestStream = await manifestEntry.OpenAsync().ConfigureAwait(false);
            await using (manifestStream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, _serializerSettings).ConfigureAwait(false);
            }
        }
    }

    private static BackupManifestDto Map(BackupManifest manifest, string path)
    {
        return new BackupManifestDto()
        {
            BackupEngineVersion = manifest.BackupEngineVersion,
            DateCreated = manifest.DateCreated,
            ServerVersion = manifest.ServerVersion,
            Path = path,
            Options = Map(manifest.Options)
        };
    }

    private static BackupOptionsDto Map(BackupOptions options)
    {
        return new BackupOptionsDto()
        {
            Metadata = options.Metadata,
            Subtitles = options.Subtitles,
            Trickplay = options.Trickplay,
            Database = options.Database
        };
    }

    private static BackupOptions Map(BackupOptionsDto options)
    {
        return new BackupOptions()
        {
            Metadata = options.Metadata,
            Subtitles = options.Subtitles,
            Trickplay = options.Trickplay,
            Database = options.Database
        };
    }

    /// <summary>
    /// Windows is able to handle '/' as a path seperator in zip files
    /// but linux isn't able to handle '\' as a path seperator in zip files,
    /// So normalize to '/'.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized path. </returns>
    private static string NormalizePathSeparator(string path)
        => path.Replace('\\', '/');
}

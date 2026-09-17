using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations;
using Emby.Server.Implementations.Configuration;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Server.Configuration;
using Jellyfin.Server.Implementations.DatabaseConfiguration;
using Jellyfin.Server.Implementations.DatabaseImport;
using Jellyfin.Server.Implementations.DatabaseImport.PostgreSql;
using Jellyfin.Server.Implementations.DatabaseImport.Sqlite;
using Jellyfin.Server.Implementations.Extensions;
using Jellyfin.Server.Migrations;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Server.DatabaseImport;

/// <summary>
/// Runs the steps of moving a SQLite database into PostgreSQL: preflight, seed, finalize and abort.
/// </summary>
/// <remarks>
/// pgloader copies the data between seed and finalize. Each step checks that it runs in order, on the database it expects,
/// and leaves the server either on its SQLite data or on a verified PostgreSQL copy of it.
/// </remarks>
internal sealed class PostgreSqlImportCommand
{
    /// <summary>
    /// The database type of the SQLite provider.
    /// </summary>
    public const string SqliteDatabaseType = "Jellyfin-SQLite";

    /// <summary>
    /// The database type of the PostgreSQL provider.
    /// </summary>
    public const string PostgreSqlDatabaseType = "Jellyfin-PostgreSQL";

    /// <summary>
    /// The name of the snapshot in the import directory, as the load file expects it.
    /// </summary>
    public const string SnapshotFileName = "jellyfin.snapshot.db";

    /// <summary>
    /// The name of the load file in the import directory.
    /// </summary>
    public const string LoadFileName = "jellyfin.load";

    private const string ManifestFileName = "manifest.json";
    private const string CatalogReferenceFileName = "catalog-reference.json";
    private const string LoadFileResource = "Jellyfin.Server.Resources.PostgreSqlImport.jellyfin.load";

    private readonly ServerApplicationPaths _paths;
    private readonly IConfiguration _startupConfiguration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Version _serverVersion;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlImportCommand"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="startupConfiguration">The startup configuration.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="serverVersion">The version of this server.</param>
    /// <param name="timeProvider">The time provider.</param>
    public PostgreSqlImportCommand(ServerApplicationPaths paths, IConfiguration startupConfiguration, ILoggerFactory loggerFactory, Version serverVersion, TimeProvider timeProvider)
    {
        _paths = paths;
        _startupConfiguration = startupConfiguration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PostgreSqlImportCommand>();
        _serverVersion = serverVersion;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Checks whether a startup mode is one of the import steps.
    /// </summary>
    /// <param name="mode">The startup mode.</param>
    /// <returns>Whether the mode runs an import step.</returns>
    public static bool IsImportMode(StartupMode? mode)
        => mode is StartupMode.PostgreSqlImportPreflight or StartupMode.PostgreSqlImportSeed or StartupMode.PostgreSqlImportFinalize or StartupMode.PostgreSqlImportAbort;

    /// <summary>
    /// Gets the path of the SQLite database a configuration uses.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    /// <param name="configuration">The database configuration.</param>
    /// <returns>The path.</returns>
    public static string GetSqliteDatabasePath(string dataPath, DatabaseConfigurationOptions configuration)
    {
        // The same rule as the SQLite provider.
        var path = configuration.CustomProviderOptions?.Options.FirstOrDefault(o => o.Key.Equals("path", StringComparison.OrdinalIgnoreCase))?.Value;
        return Path.GetFullPath(path ?? Path.Combine(dataPath, "jellyfin.db"));
    }

    /// <summary>
    /// Runs an import step.
    /// </summary>
    /// <param name="mode">The step.</param>
    /// <param name="importDirectory">The import directory given on the command line, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> RunAsync(StartupMode mode, string? importDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return mode switch
            {
                StartupMode.PostgreSqlImportPreflight => await PreflightAsync(importDirectory, cancellationToken).ConfigureAwait(false),
                StartupMode.PostgreSqlImportSeed => await SeedAsync(importDirectory, cancellationToken).ConfigureAwait(false),
                StartupMode.PostgreSqlImportFinalize => await FinalizeAsync(importDirectory, cancellationToken).ConfigureAwait(false),
                StartupMode.PostgreSqlImportAbort => await AbortAsync(importDirectory, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not an import step.")
            };
        }
        catch (RefusedException ex)
        {
            _logger.LogCritical("{Message}", ex.Message);
            return ImportExitCode.Refused;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The PostgreSQL import step {Step} failed.", mode);
            return ImportExitCode.Error;
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task<T> ReadFileAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            throw new RefusedException($"'{path}' is missing. Run the import steps again from preflight.");
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return typeof(T) == typeof(ImportManifest)
                ? (T)(object)await ImportJson.ReadManifestAsync(stream, cancellationToken).ConfigureAwait(false)
                : await ImportJson.ReadAsync<T>(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteFileAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            await ImportJson.WriteAsync(stream, value, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> PreflightAsync(string? importDirectory, CancellationToken cancellationToken)
    {
        var startedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var state = await ImportState.ReadAsync(_paths.DataPath, cancellationToken).ConfigureAwait(false);
        if (state is { Step: not ImportStage.Preflighted })
        {
            throw new RefusedException($"The import is past preflight ({state.Step}). Finish it with 'jellyfin --mode PostgreSqlImportFinalize' or cancel it with 'jellyfin --mode PostgreSqlImportAbort'.");
        }

        var configuration = ReadDatabaseConfiguration();
        if (!string.Equals(configuration.DatabaseType, SqliteDatabaseType, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLUGIN_PROVIDER")))
        {
            throw new RefusedException($"Preflight reads the SQLite database, but the server is configured for '{configuration.DatabaseType}'. Run preflight before changing database.xml.");
        }

        var livePath = GetSqliteDatabasePath(_paths.DataPath, configuration);
        if (!File.Exists(livePath))
        {
            throw new RefusedException($"The SQLite database '{livePath}' does not exist.");
        }

        var directory = Path.GetFullPath(importDirectory ?? state?.ImportDirectory ?? Path.Combine(_paths.DataPath, "postgresql-import"));
        Directory.CreateDirectory(directory);
        var snapshotPath = Path.Combine(directory, SnapshotFileName);
        File.Delete(snapshotPath);

        var liveSize = SqliteSnapshotWriter.DatabaseFileSuffixes.Select(s => new FileInfo(livePath + s)).Where(f => f.Exists).Sum(f => f.Length);
        var freeSpace = new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
        if (freeSpace < liveSize * 12 / 10)
        {
            throw new RefusedException($"The import directory '{directory}' needs {liveSize * 12 / 10} bytes free for the snapshot, but has {freeSpace}.");
        }

        _logger.LogInformation("Copying the SQLite database {Path} to {Snapshot}", livePath, snapshotPath);
        var snapshot = await SqliteSnapshotWriter.WriteAsync(livePath, snapshotPath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Checking the snapshot");
        var model = ImportModel.ForPostgreSql();
        var inspector = new SqliteSourceInspector(model, SqliteSourceInspector.GetSchemaMigrationIds(), JellyfinMigrationService.GetCodeMigrationIds(), _serverVersion);
        SqliteInspection inspection;
        var connection = await SqliteSourceInspector.OpenReadOnlyAsync(snapshot.Path, cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            inspection = await inspector.InspectAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        await WriteReportAsync(directory, "preflight", new ImportReport(ImportReport.CurrentFormatVersion, Implementations.DatabaseImport.ImportStep.Preflight, _serverVersion.ToString(), startedUtc, _timeProvider.GetUtcNow().UtcDateTime, inspection.Findings), cancellationToken).ConfigureAwait(false);
        if (!inspection.Succeeded)
        {
            File.Delete(snapshot.Path);
            ImportState.Delete(_paths.DataPath);
            return ImportExitCode.ChecksFailed;
        }

        var manifest = new ImportManifest(
            ImportManifest.CurrentFormatVersion,
            _serverVersion.ToString(),
            model.Fingerprint,
            snapshot.Sha256,
            snapshot.SourceFiles,
            inspection.Tables,
            inspection.Findings);
        await WriteFileAsync(Path.Combine(directory, ManifestFileName), manifest, cancellationToken).ConfigureAwait(false);
        await WriteLoadFileAsync(Path.Combine(directory, LoadFileName), cancellationToken).ConfigureAwait(false);
        await new ImportState(ImportState.CurrentFormatVersion, ImportStage.Preflighted, directory, livePath, snapshot.Sha256, _serverVersion.ToString(), null, _timeProvider.GetUtcNow().UtcDateTime)
            .WriteAsync(_paths.DataPath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Preflight passed. Point database.xml at an empty PostgreSQL database owned by the Jellyfin role and run 'jellyfin --mode PostgreSqlImportSeed'. The server does not start until the import is finished or aborted.");
        return ImportExitCode.Success;
    }

    private async Task<int> SeedAsync(string? importDirectory, CancellationToken cancellationToken)
    {
        var state = await RequireStateAsync(ImportStage.Preflighted, importDirectory, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadFileAsync<ImportManifest>(Path.Combine(state.ImportDirectory, ManifestFileName), cancellationToken).ConfigureAwait(false);
        var model = ImportModel.ForPostgreSql();
        RequireSameBuild(manifest, model);

        var services = BuildDatabaseServices(PostgreSqlDatabaseType);
        await using (services.ConfigureAwait(false))
        {
            var context = await services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var connection = (NpgsqlConnection)context.Database.GetDbConnection();
                await OpenTargetAsync(context, cancellationToken).ConfigureAwait(false);
                var objects = await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_class WHERE relnamespace = current_schema()::regnamespace", cancellationToken).ConfigureAwait(false);
                if (objects > 0)
                {
                    throw new RefusedException($"The PostgreSQL database '{connection.Database}' is not empty ({objects} objects in its schema). The import needs a new, empty database.");
                }

                _logger.LogInformation("Creating the schema in {Database}", connection.Database);
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

                // The source ran every code migration of this build, so the target records all of them as applied.
                var history = context.GetService<IHistoryRepository>();
                foreach (var id in JellyfinMigrationService.GetCodeMigrationIds())
                {
#pragma warning disable EF1002 // The script comes from the history repository.
                    await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, _serverVersion.ToString())), cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
                }

                var reference = await PostgreSqlCatalogSnapshot.CaptureAsync(connection, cancellationToken).ConfigureAwait(false);
                await WriteFileAsync(Path.Combine(state.ImportDirectory, CatalogReferenceFileName), reference, cancellationToken).ConfigureAwait(false);
                await (state with { Step = ImportStage.Seeded, DatabaseOid = reference.DatabaseOid, UpdatedUtc = _timeProvider.GetUtcNow().UtcDateTime })
                    .WriteAsync(_paths.DataPath, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation(
            "Seed completed. Load the data with pgloader using {LoadFile}, then run 'jellyfin --mode PostgreSqlImportFinalize'.",
            Path.Combine(state.ImportDirectory, LoadFileName));
        return ImportExitCode.Success;
    }

    private async Task<int> FinalizeAsync(string? importDirectory, CancellationToken cancellationToken)
    {
        var startedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var state = await RequireStateAsync(ImportStage.Seeded, importDirectory, cancellationToken).ConfigureAwait(false);
        if (state.Step == ImportStage.Seeded)
        {
            var manifest = await ReadFileAsync<ImportManifest>(Path.Combine(state.ImportDirectory, ManifestFileName), cancellationToken).ConfigureAwait(false);
            var reference = await ReadFileAsync<PostgreSqlCatalogSnapshot>(Path.Combine(state.ImportDirectory, CatalogReferenceFileName), cancellationToken).ConfigureAwait(false);
            var model = ImportModel.ForPostgreSql();
            RequireSameBuild(manifest, model);

            var snapshotPath = Path.Combine(state.ImportDirectory, SnapshotFileName);
            if (!File.Exists(snapshotPath) || await HashFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false) != manifest.SnapshotSha256)
            {
                throw new RefusedException($"The snapshot '{snapshotPath}' changed after preflight. Abort the import and start again from preflight.");
            }

            if (!SqliteSnapshotWriter.ReadSourceFiles(state.SqliteDatabasePath).SequenceEqual(manifest.SourceFiles))
            {
                throw new RefusedException($"The SQLite database '{state.SqliteDatabasePath}' changed after preflight, so the snapshot no longer has all of its data. Abort the import and start again from preflight.");
            }

            var services = BuildDatabaseServices(PostgreSqlDatabaseType);
            await using (services.ConfigureAwait(false))
            {
                var context = await services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using (context.ConfigureAwait(false))
                {
                    var historyIds = context.Database.GetMigrations().Concat(JellyfinMigrationService.GetCodeMigrationIds());
                    await OpenTargetAsync(context, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Verifying the loaded data");
                    var result = await new PostgreSqlImportFinalizer(model, historyIds, reference)
                        .FinalizeAsync((NpgsqlConnection)context.Database.GetDbConnection(), manifest, cancellationToken).ConfigureAwait(false);
                    await WriteReportAsync(state.ImportDirectory, "finalize", new ImportReport(ImportReport.CurrentFormatVersion, Implementations.DatabaseImport.ImportStep.Finalize, _serverVersion.ToString(), startedUtc, _timeProvider.GetUtcNow().UtcDateTime, result.Findings), cancellationToken).ConfigureAwait(false);
                    if (!result.Committed)
                    {
                        _logger.LogCritical("The loaded data does not match the SQLite database. Nothing was committed; see the report in {Directory}.", state.ImportDirectory);
                        return ImportExitCode.ChecksFailed;
                    }
                }
            }

            state = state with { Step = ImportStage.Committed, UpdatedUtc = _timeProvider.GetUtcNow().UtcDateTime };
            await state.WriteAsync(_paths.DataPath, cancellationToken).ConfigureAwait(false);
        }

        // Set the SQLite files aside so a start on SQLite cannot silently begin with an empty database.
        foreach (var suffix in SqliteSnapshotWriter.DatabaseFileSuffixes)
        {
            var path = state.SqliteDatabasePath + suffix;
            if (File.Exists(path))
            {
                File.Move(path, state.SqliteDatabasePath + DatabaseImportGuard.ImportedSuffix + suffix);
            }
        }

        ImportState.Delete(_paths.DataPath);
        _logger.LogInformation(
            "The import is complete. The SQLite database was renamed to {Path}. Start the server normally and back up the PostgreSQL database.",
            state.SqliteDatabasePath + DatabaseImportGuard.ImportedSuffix);
        return ImportExitCode.Success;
    }

    private async Task<int> AbortAsync(string? importDirectory, CancellationToken cancellationToken)
    {
        var state = await ImportState.ReadAsync(_paths.DataPath, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            _logger.LogInformation("No PostgreSQL import is in progress.");
            return ImportExitCode.Success;
        }

        if (importDirectory is not null && !string.Equals(Path.GetFullPath(importDirectory), state.ImportDirectory, StringComparison.Ordinal))
        {
            throw new RefusedException($"The import in progress uses '{state.ImportDirectory}', not '{importDirectory}'.");
        }

        if (state.Step == ImportStage.Committed)
        {
            throw new RefusedException("The imported data is already committed to PostgreSQL. Run 'jellyfin --mode PostgreSqlImportFinalize' to finish the import.");
        }

        // PostgreSQL is left untouched; the SQLite files were never changed.
        File.Delete(Path.Combine(state.ImportDirectory, SnapshotFileName));
        ImportState.Delete(_paths.DataPath);
        _logger.LogInformation("The import was aborted. If database.xml points at PostgreSQL, point it back at SQLite before starting the server. The reports stay in {Directory}.", state.ImportDirectory);
        return ImportExitCode.Success;
    }

    private async Task<ImportState> RequireStateAsync(ImportStage step, string? importDirectory, CancellationToken cancellationToken)
    {
        var state = await ImportState.ReadAsync(_paths.DataPath, cancellationToken).ConfigureAwait(false)
            ?? throw new RefusedException("No PostgreSQL import is in progress. Start with 'jellyfin --mode PostgreSqlImportPreflight' while the server is configured for SQLite.");
        if (importDirectory is not null && !string.Equals(Path.GetFullPath(importDirectory), state.ImportDirectory, StringComparison.Ordinal))
        {
            throw new RefusedException($"The import in progress uses '{state.ImportDirectory}', not '{importDirectory}'.");
        }

        var allowed = step == ImportStage.Seeded ? state.Step is ImportStage.Seeded or ImportStage.Committed : state.Step == step;
        if (!allowed)
        {
            throw new RefusedException($"This step needs the import to be {step}, but it is {state.Step}.");
        }

        if (state.ServerVersion != _serverVersion.ToString())
        {
            throw new RefusedException($"The import was started by server {state.ServerVersion}; run every step with the same server version, or abort and start again.");
        }

        return state;
    }

    private void RequireSameBuild(ImportManifest manifest, ImportModel model)
    {
        if (manifest.ServerVersion != _serverVersion.ToString() || manifest.ModelFingerprint != model.Fingerprint)
        {
            throw new RefusedException($"Preflight ran on server {manifest.ServerVersion} with a different database model. Abort the import and start again with this server.");
        }
    }

    private DatabaseConfigurationOptions ReadDatabaseConfiguration()
    {
        var configurationManager = new ServerConfigurationManager(_paths, _loggerFactory, new MyXmlSerializer());
        configurationManager.AddParts([new DatabaseConfigurationFactory()]);
        return ServiceCollectionExtensions.ResolveDatabaseConfiguration(configurationManager, _startupConfiguration);
    }

    private ServiceProvider BuildDatabaseServices(string databaseType)
    {
        var configuration = ReadDatabaseConfiguration();
        if (!string.Equals(configuration.DatabaseType, databaseType, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLUGIN_PROVIDER")))
        {
            throw new RefusedException($"This step writes to PostgreSQL, but the server is configured for '{configuration.DatabaseType}'. Set DatabaseType to '{databaseType}' in database.xml; the PostgreSQL plugin cannot be used for the import.");
        }

        var configurationManager = new ServerConfigurationManager(_paths, _loggerFactory, new MyXmlSerializer());
        configurationManager.AddParts([new DatabaseConfigurationFactory()]);
        var services = new ServiceCollection()
            .AddSingleton(_loggerFactory)
            .AddLogging()
            .AddJellyfinDbContext(configurationManager, _startupConfiguration)
            .AddSingleton<IApplicationPaths>(_paths)
            .BuildServiceProvider();
        services.GetRequiredService<IJellyfinDatabaseProvider>().DbContextFactory = services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        return services;
    }

    private async Task OpenTargetAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            throw new RefusedException($"The PostgreSQL database does not exist ({ex.MessageText}). Create an empty database owned by the Jellyfin role and run the step again.");
        }
        catch (InvalidOperationException ex)
        {
            // The provider's startup checks refuse unsuitable servers, such as an old version or a non-UTF8 database.
            throw new RefusedException(ex.Message);
        }
    }

    private async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // The statements are constants of this class.
        var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using (command.ConfigureAwait(false))
        {
            return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }

    private async Task WriteReportAsync(string directory, string name, ImportReport report, CancellationToken cancellationToken)
    {
        await WriteFileAsync(Path.Combine(directory, name + "-report.json"), report, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, name + "-report.txt"), ImportReportText.Format(report), cancellationToken).ConfigureAwait(false);
        foreach (var finding in report.Findings)
        {
            _logger.Log(finding.Severity == ImportFindingSeverity.Error ? LogLevel.Error : LogLevel.Warning, "{Finding}", ImportReportText.Describe(finding));
        }

        _logger.LogInformation("The {Step} report was written to {Path}", report.Step, Path.Combine(directory, name + "-report.txt"));
    }

    private async Task WriteLoadFileAsync(string path, CancellationToken cancellationToken)
    {
        var resource = typeof(PostgreSqlImportCommand).Assembly.GetManifestResourceStream(LoadFileResource)
            ?? throw new InvalidOperationException($"The resource {LoadFileResource} is missing.");
        await using (resource.ConfigureAwait(false))
        {
            var file = File.Create(path);
            await using (file.ConfigureAwait(false))
            {
                await resource.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A step was refused before it changed anything.
    /// </summary>
    private sealed class RefusedException : Exception
    {
        public RefusedException(string message)
            : base(message)
        {
        }
    }
}

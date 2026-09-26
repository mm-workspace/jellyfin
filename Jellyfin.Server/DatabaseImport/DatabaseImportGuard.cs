using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Server.Implementations.DatabaseImport.Sqlite;

namespace Jellyfin.Server.DatabaseImport;

/// <summary>
/// Keeps the server from starting while a PostgreSQL import is between steps, and on the SQLite files an import set aside.
/// </summary>
internal static class DatabaseImportGuard
{
    /// <summary>
    /// The suffix added to the SQLite database files once their data is committed to PostgreSQL.
    /// </summary>
    public const string ImportedSuffix = ".imported-to-postgresql";

    /// <summary>
    /// Throws if the server must not start.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    /// <param name="databaseConfiguration">The database configuration the server would use.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the check.</returns>
    /// <exception cref="InvalidOperationException">An import is in progress, or the SQLite database was imported.</exception>
    public static async Task EnsureNoImportInProgressAsync(string dataPath, DatabaseConfigurationOptions databaseConfiguration, CancellationToken cancellationToken)
    {
        var state = await ImportState.ReadAsync(dataPath, cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            throw new InvalidOperationException(state.Step switch
            {
                ImportStage.Preflighted => $"A PostgreSQL import is prepared in '{state.ImportDirectory}'. Point database.xml at the empty PostgreSQL database and run 'jellyfin --mode PostgreSqlImportSeed', or cancel it with 'jellyfin --mode PostgreSqlImportAbort'.",
                ImportStage.Seeded => "A PostgreSQL import is waiting for its data. Load it with pgloader and run 'jellyfin --mode PostgreSqlImportFinalize', or cancel it with 'jellyfin --mode PostgreSqlImportAbort' and point database.xml back at SQLite.",
                _ => "A PostgreSQL import was committed but not completed. Run 'jellyfin --mode PostgreSqlImportFinalize' again to finish it."
            });
        }

        if (string.Equals(databaseConfiguration.DatabaseType, PostgreSqlImportCommand.SqliteDatabaseType, StringComparison.OrdinalIgnoreCase))
        {
            var sqlitePath = PostgreSqlImportCommand.GetSqliteDatabasePath(dataPath, databaseConfiguration);
            if (!File.Exists(sqlitePath) && FindImportedPaths(sqlitePath) is { Count: > 0 } importedPaths)
            {
                throw new InvalidOperationException(
                    $"The SQLite database '{sqlitePath}' was imported into PostgreSQL and set aside as '{string.Join("', '", importedPaths)}'. Point database.xml at the PostgreSQL database; starting on SQLite would begin with an empty server.");
            }
        }
    }

    /// <summary>
    /// Gets a path to set a SQLite database aside at that no file of an earlier import uses.
    /// </summary>
    /// <param name="sqlitePath">The path of the SQLite database.</param>
    /// <returns>The new path of the database file; its write-ahead log files keep their suffixes after it.</returns>
    public static string GetFreeImportedPath(string sqlitePath)
    {
        // A database that was imported, copied back and imported again keeps the copy set aside the first time.
        var path = sqlitePath + ImportedSuffix;
        for (var number = 2; SqliteSnapshotWriter.DatabaseFileSuffixes.Any(suffix => File.Exists(path + suffix)); number++)
        {
            path = string.Create(CultureInfo.InvariantCulture, $"{sqlitePath}{ImportedSuffix}.{number}");
        }

        return path;
    }

    /// <summary>
    /// Finds the SQLite database files that imports set aside in place of a database.
    /// </summary>
    /// <param name="sqlitePath">The path of the SQLite database.</param>
    /// <returns>The paths of the database files set aside, without their write-ahead log files.</returns>
    public static IReadOnlyList<string> FindImportedPaths(string sqlitePath)
    {
        var directory = Path.GetDirectoryName(sqlitePath);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, Path.GetFileName(sqlitePath) + ImportedSuffix + "*")
            .Where(path => !SqliteSnapshotWriter.DatabaseFileSuffixes.Any(suffix => suffix.Length > 0 && path.EndsWith(suffix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;

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
            if (!File.Exists(sqlitePath) && File.Exists(sqlitePath + ImportedSuffix))
            {
                throw new InvalidOperationException(
                    $"The SQLite database '{sqlitePath}' was imported into PostgreSQL and set aside as '{sqlitePath + ImportedSuffix}'. Point database.xml at the PostgreSQL database; starting on SQLite would begin with an empty server.");
            }
        }
    }
}

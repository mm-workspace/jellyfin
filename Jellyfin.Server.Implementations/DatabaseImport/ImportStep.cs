namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// A step of the SQLite to PostgreSQL import.
/// </summary>
internal enum ImportStep
{
    /// <summary>
    /// Checks the SQLite database and takes the snapshot that is loaded.
    /// </summary>
    Preflight,

    /// <summary>
    /// Creates the schema in the empty PostgreSQL database.
    /// </summary>
    Seed,

    /// <summary>
    /// Verifies the loaded data and commits it.
    /// </summary>
    Finalize
}

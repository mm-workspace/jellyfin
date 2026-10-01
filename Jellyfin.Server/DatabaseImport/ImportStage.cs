namespace Jellyfin.Server.DatabaseImport;

/// <summary>
/// How far a PostgreSQL import has come.
/// </summary>
internal enum ImportStage
{
    /// <summary>
    /// The SQLite database passed preflight and its snapshot is ready to load.
    /// </summary>
    Preflighted,

    /// <summary>
    /// The PostgreSQL database has the schema and is waiting for pgloader.
    /// </summary>
    Seeded,

    /// <summary>
    /// The loaded data was committed; the SQLite files still have to be set aside.
    /// </summary>
    Committed
}

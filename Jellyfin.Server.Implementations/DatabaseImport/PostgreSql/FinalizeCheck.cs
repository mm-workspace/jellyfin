namespace Jellyfin.Server.Implementations.DatabaseImport.PostgreSql;

/// <summary>
/// The checks of a loaded PostgreSQL database before the import is committed.
/// </summary>
internal enum FinalizeCheck
{
    /// <summary>
    /// Another session holds the import lock.
    /// </summary>
    ImportLockHeld,

    /// <summary>
    /// The database is not the one that was seeded.
    /// </summary>
    TargetChanged,

    /// <summary>
    /// Migrations of this server are missing from the history.
    /// </summary>
    HistoryMissingMigrations,

    /// <summary>
    /// The history has migrations this server does not have, such as those of a plugin.
    /// </summary>
    HistoryForeignMigrations,

    /// <summary>
    /// A table, column, constraint, index or trigger differs from the seeded schema.
    /// </summary>
    CatalogChanged,

    /// <summary>
    /// A table has a different number of rows than the source.
    /// </summary>
    RowCountMismatch,

    /// <summary>
    /// A timestamp column has a different number of values outside the range of the server than the source.
    /// </summary>
    SentinelCountMismatch,

    /// <summary>
    /// The rows of a table differ from the source.
    /// </summary>
    ContentMismatch,

    /// <summary>
    /// An identity sequence would hand out an id that exists.
    /// </summary>
    IdentitySequenceBehind
}

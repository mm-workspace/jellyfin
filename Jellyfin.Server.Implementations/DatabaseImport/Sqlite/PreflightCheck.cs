namespace Jellyfin.Server.Implementations.DatabaseImport.Sqlite;

/// <summary>
/// The checks of a SQLite source before it is imported.
/// </summary>
internal enum PreflightCheck
{
    /// <summary>
    /// The database has no migration history.
    /// </summary>
    MissingHistory,

    /// <summary>
    /// Migrations of this server have not run on the database.
    /// </summary>
    PendingMigrations,

    /// <summary>
    /// The database has migrations of a newer server.
    /// </summary>
    NewerMigrations,

    /// <summary>
    /// The database has migrations this server no longer has; they are not imported.
    /// </summary>
    RetiredMigrations,

    /// <summary>
    /// A table of the model is missing.
    /// </summary>
    MissingTable,

    /// <summary>
    /// A table that is not part of the model, such as a plugin table; it is not imported.
    /// </summary>
    UnknownTable,

    /// <summary>
    /// A column of the model is missing.
    /// </summary>
    MissingColumn,

    /// <summary>
    /// A column that is not part of the model.
    /// </summary>
    UnknownColumn,

    /// <summary>
    /// Rows reference rows that do not exist.
    /// </summary>
    Orphans,

    /// <summary>
    /// Values are stored with a storage class the column type does not allow.
    /// </summary>
    StorageClassMismatch,

    /// <summary>
    /// A column that does not allow NULL holds NULL.
    /// </summary>
    NullInRequiredColumn,

    /// <summary>
    /// Text is not valid UTF-8.
    /// </summary>
    InvalidUtf8,

    /// <summary>
    /// Text contains a NUL character, which PostgreSQL cannot store.
    /// </summary>
    NulInText,

    /// <summary>
    /// An id is not a GUID in the 8-4-4-4-12 format.
    /// </summary>
    InvalidUuid,

    /// <summary>
    /// A number does not fit the 32-bit column.
    /// </summary>
    IntegerOutOfRange,

    /// <summary>
    /// A boolean is not 0 or 1.
    /// </summary>
    InvalidBoolean,

    /// <summary>
    /// A timestamp cannot be parsed.
    /// </summary>
    InvalidTimestamp,

    /// <summary>
    /// A timestamp carries a time zone offset.
    /// </summary>
    TimestampWithOffset,

    /// <summary>
    /// Keyframe ticks are not a JSON array of numbers.
    /// </summary>
    InvalidKeyframeTicks,

    /// <summary>
    /// Keys that are different in SQLite are equal in PostgreSQL, such as ids differing in case.
    /// </summary>
    DuplicateKeyAfterConversion,

    /// <summary>
    /// The indexed values of a row are too large for a PostgreSQL B-tree index.
    /// </summary>
    IndexRowTooLarge
}

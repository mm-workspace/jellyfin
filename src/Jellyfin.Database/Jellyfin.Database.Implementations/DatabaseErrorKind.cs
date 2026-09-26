namespace Jellyfin.Database.Implementations;

/// <summary>
/// Defines the kinds of database failure shared code can react to without knowing the error codes of the database
/// engine it runs on.
/// </summary>
public enum DatabaseErrorKind
{
    /// <summary>
    /// The failure is none of the kinds below, or the provider does not recognise it.
    /// </summary>
    None = 0,

    /// <summary>
    /// The operation did not run because something else held what it needed, or because the connection to the
    /// database was lost. Running it again may well succeed.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// A row conflicts with one that is already stored, on a primary key or on a unique index. Running the same
    /// operation again fails the same way.
    /// </summary>
    UniqueViolation = 2,

    /// <summary>
    /// The database picked this operation as the victim of a deadlock and rolled it back.
    /// </summary>
    Deadlock = 3
}

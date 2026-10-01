namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// How a finding affects the import.
/// </summary>
internal enum ImportFindingSeverity
{
    /// <summary>
    /// The import continues; the finding is reported.
    /// </summary>
    Warning,

    /// <summary>
    /// The import stops.
    /// </summary>
    Error
}

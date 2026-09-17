namespace Jellyfin.Server.DatabaseImport;

/// <summary>
/// The exit codes of the PostgreSQL import modes.
/// </summary>
internal static class ImportExitCode
{
    /// <summary>
    /// The step completed.
    /// </summary>
    public const int Success = 0;

    /// <summary>
    /// The step failed unexpectedly.
    /// </summary>
    public const int Error = 1;

    /// <summary>
    /// The step was refused before it changed anything, for example because it runs in the wrong order or on the wrong database.
    /// </summary>
    public const int Refused = 3;

    /// <summary>
    /// The data did not pass the checks of the step; the report in the import directory lists why.
    /// </summary>
    public const int ChecksFailed = 4;
}

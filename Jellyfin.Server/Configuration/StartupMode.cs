using MediaBrowser.Model.Configuration;

namespace Jellyfin.Server.Configuration;

/// <summary>
/// Defines types for usage with the <see cref="StartupOptions.StartupMode"/>.
/// </summary>
public enum StartupMode
{
    /// <summary>
    /// Default startup mode, runs the jellyfin server in normal operation.
    /// </summary>
    MediaServer = 0,

    /// <summary>
    /// Attempts to Migrate the system only then shuts down.
    /// </summary>
    MigrateSystem = 1,

    /// <summary>
    /// Runs the Database seed function regardless of <see cref="BaseApplicationConfiguration.IsStartupWizardCompleted"/> state.
    /// </summary>
    SeedSystem = 2,

    /// <summary>
    /// Checks the SQLite database and prepares it for an import into PostgreSQL, then shuts down.
    /// </summary>
    PostgreSqlImportPreflight = 3,

    /// <summary>
    /// Creates the schema in the empty PostgreSQL database of an import, then shuts down.
    /// </summary>
    PostgreSqlImportSeed = 4,

    /// <summary>
    /// Verifies the data pgloader loaded into PostgreSQL and completes the import, then shuts down.
    /// </summary>
    PostgreSqlImportFinalize = 5,

    /// <summary>
    /// Cancels an import that is not committed, leaving PostgreSQL untouched, then shuts down.
    /// </summary>
    PostgreSqlImportAbort = 6
}

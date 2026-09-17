using Jellyfin.Server.Configuration;

namespace Jellyfin.Server.Helpers;

/// <summary>
/// Describes how the process ends when the server fails to start.
/// </summary>
/// <param name="SetFailureExitCode">Whether the process exits with a non-zero exit code.</param>
/// <param name="ShowErrorBeforeExit">Whether the setup server keeps showing the error for a while before the process exits.</param>
internal readonly record struct StartupFailureHandling(bool SetFailureExitCode, bool ShowErrorBeforeExit)
{
    /// <summary>
    /// Gets the handling for a failed startup.
    /// </summary>
    /// <param name="startupMode">The startup mode the server was started with.</param>
    /// <param name="startupMigrationFailed">Whether the failure happened while applying the startup migrations.</param>
    /// <returns>The handling to apply.</returns>
    public static StartupFailureHandling For(StartupMode? startupMode, bool startupMigrationFailed)
    {
        var runsMediaServer = RunsMediaServer(startupMode);

        // A one-off maintenance run is started from a script or a terminal, so it has to report the failure through its exit code.
        // A failure of the startup migrations used to end the process with an unhandled exception, so it keeps failing the process.
        return new StartupFailureHandling(
            SetFailureExitCode: startupMigrationFailed || !runsMediaServer,
            ShowErrorBeforeExit: runsMediaServer);
    }

    /// <summary>
    /// Gets a value indicating whether the startup mode runs the media server rather than a one-off maintenance task.
    /// </summary>
    /// <param name="startupMode">The startup mode the server was started with.</param>
    /// <returns><c>true</c> if the media server runs; otherwise <c>false</c>.</returns>
    public static bool RunsMediaServer(StartupMode? startupMode) => startupMode is null or StartupMode.MediaServer;
}

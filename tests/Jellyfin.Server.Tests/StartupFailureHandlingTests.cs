using Jellyfin.Server.Configuration;
using Jellyfin.Server.Helpers;
using Xunit;

namespace Jellyfin.Server.Tests;

public class StartupFailureHandlingTests
{
    [Theory]
    [InlineData(null, false, false, true)]
    [InlineData(StartupMode.MediaServer, false, false, true)]
    [InlineData(StartupMode.MigrateSystem, false, true, false)]
    [InlineData(StartupMode.SeedSystem, false, true, false)]
    [InlineData(null, true, true, true)]
    [InlineData(StartupMode.MediaServer, true, true, true)]
    [InlineData(StartupMode.MigrateSystem, true, true, false)]
    [InlineData(StartupMode.SeedSystem, true, true, false)]
    public void For_ModeAndFailure_ReturnsHandling(StartupMode? startupMode, bool startupMigrationFailed, bool setFailureExitCode, bool showErrorBeforeExit)
    {
        var handling = StartupFailureHandling.For(startupMode, startupMigrationFailed);

        Assert.Equal(setFailureExitCode, handling.SetFailureExitCode);
        Assert.Equal(showErrorBeforeExit, handling.ShowErrorBeforeExit);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(StartupMode.MediaServer, true)]
    [InlineData(StartupMode.MigrateSystem, false)]
    [InlineData(StartupMode.SeedSystem, false)]
    public void RunsMediaServer_Mode_ReturnsExpected(StartupMode? startupMode, bool expected)
    {
        Assert.Equal(expected, StartupFailureHandling.RunsMediaServer(startupMode));
    }
}

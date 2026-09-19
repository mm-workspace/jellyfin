using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Server.DatabaseImport;
using Xunit;

namespace Jellyfin.Server.Tests.DatabaseImport;

public sealed class ImportFreeSpaceTests : IDisposable
{
    // Other processes write to the same file system between two measurements.
    private const long Tolerance = 256L * 1024 * 1024;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jf-import-free-space-" + Guid.NewGuid().ToString("N"));

    public ImportFreeSpaceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void FreeSpacePath_OnUnix_IsTheDirectoryNotTheRootFileSystem()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test");

        // This is what keeps preflight measuring the import directory: in a container, /config is usually a bind mount with a
        // file system of its own, and the root of the path would measure the container's root file system instead.
        Assert.Equal("/config/data/postgresql-import", PostgreSqlImportCommand.GetFreeSpacePath("/config/data/postgresql-import"));
    }

    [Theory]
    [InlineData(@"D:\jellyfin\data\postgresql-import", @"D:\")]
    [InlineData(@"\\?\D:\jellyfin\data\postgresql-import", @"D:\")]
    [InlineData(@"\\.\D:\jellyfin\data\postgresql-import", @"D:\")]
    [InlineData(@"\\nas\media\postgresql-import", null)]
    [InlineData(@"\\?\UNC\nas\media\postgresql-import", null)]
    public void FreeSpacePath_OnWindows_IsTheDriveOrNothingForAShare(string directory, string? expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test");

        Assert.Equal(expected, PostgreSqlImportCommand.GetFreeSpacePath(directory));
    }

    [Fact]
    public async Task AvailableFreeSpace_AgreesWithDf()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "df is a Unix tool");

        // Checks that the free space is read the way df reads it. The temporary directory is usually on the root file system,
        // where measuring the root instead gives the same answer; /dev is a file system of its own on Linux and macOS.
        foreach (var directory in new[] { _directory, "/dev" })
        {
            using var df = Process.Start(new ProcessStartInfo("df", ["-Pk", directory]) { RedirectStandardOutput = true })!;
            var output = await df.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await df.WaitForExitAsync(TestContext.Current.CancellationToken);
            var measured = PostgreSqlImportCommand.GetAvailableFreeSpace(directory);

            // File system, 1024-blocks, used, available, capacity and mount point; only the first and last may contain spaces.
            var fields = output.Trim().Split('\n')[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var available = long.Parse(fields[Array.FindLastIndex(fields, f => f.EndsWith('%')) - 1], CultureInfo.InvariantCulture) * 1024;
            Assert.NotNull(measured);
            Assert.InRange(measured.Value, available - Tolerance, available + Tolerance);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }
}

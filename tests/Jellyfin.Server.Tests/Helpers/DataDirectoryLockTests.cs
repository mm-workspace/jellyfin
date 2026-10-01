using System;
using System.IO;
using Jellyfin.Server.Helpers;
using Xunit;

namespace Jellyfin.Server.Tests.Helpers;

public sealed class DataDirectoryLockTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "jellyfin-data-lock-tests", Guid.NewGuid().ToString("N"));

    public DataDirectoryLockTests()
    {
        Directory.CreateDirectory(_dataPath);
    }

    [Fact]
    public void TryAcquire_FreeDirectory_Acquires()
    {
        var result = DataDirectoryLock.TryAcquire(_dataPath);
        using var dataDirectoryLock = result.Lock;

        Assert.Equal(DataDirectoryLockStatus.Acquired, result.Status);
        Assert.NotNull(dataDirectoryLock);
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(Path.Combine(_dataPath, DataDirectoryLock.HolderFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void TryAcquire_AlreadyLocked_ReportsTheHolder()
    {
        using var first = DataDirectoryLock.TryAcquire(_dataPath).Lock;

        var second = DataDirectoryLock.TryAcquire(_dataPath);

        Assert.Equal(DataDirectoryLockStatus.Held, second.Status);
        Assert.Null(second.Lock);
        Assert.NotNull(second.Holder);
        Assert.Contains("process " + Environment.ProcessId, second.Holder, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_ReleasesTheLock()
    {
        DataDirectoryLock.TryAcquire(_dataPath).Lock!.Dispose();

        var result = DataDirectoryLock.TryAcquire(_dataPath);
        using var dataDirectoryLock = result.Lock;

        Assert.Equal(DataDirectoryLockStatus.Acquired, result.Status);
    }

    [Fact]
    public void TryAcquire_UnwritableDirectory_IsUnsupported()
    {
        Assert.SkipWhen(Environment.UserName == "root", "Permissions do not apply to root.");
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix file permissions.");
        }
        else
        {
            File.SetUnixFileMode(_dataPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var result = DataDirectoryLock.TryAcquire(_dataPath);

        Assert.Equal(DataDirectoryLockStatus.Unsupported, result.Status);
        Assert.Null(result.Lock);
        Assert.NotNull(result.Error);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_dataPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_dataPath, true);
    }
}

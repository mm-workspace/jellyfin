using System;
using System.Globalization;
using System.IO;

namespace Jellyfin.Server.Helpers;

/// <summary>
/// An exclusive lock on the data directory, so that two servers never use the same data at the same time.
/// </summary>
internal sealed class DataDirectoryLock : IDisposable
{
    /// <summary>
    /// The name of the file that is kept locked in the data directory.
    /// </summary>
    internal const string LockFileName = "jellyfin.lock";

    /// <summary>
    /// The name of the file that describes the process holding the lock. The lock file itself cannot be read while it is locked.
    /// </summary>
    internal const string HolderFileName = "jellyfin.lock.holder";

    private readonly FileStream _lockFile;

    private DataDirectoryLock(FileStream lockFile)
    {
        _lockFile = lockFile;
    }

    /// <summary>
    /// Tries to lock a data directory.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    /// <returns>The result.</returns>
    public static DataDirectoryLockResult TryAcquire(string dataPath)
    {
        if (AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var disabled) && disabled)
        {
            return new DataDirectoryLockResult(DataDirectoryLockStatus.Unsupported, null, null, new NotSupportedException("File locking is disabled for this process."));
        }

        var lockPath = Path.Combine(dataPath, LockFileName);
        var holderPath = Path.Combine(dataPath, HolderFileName);
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsLockedByAnotherHandle(ex))
        {
            return new DataDirectoryLockResult(DataDirectoryLockStatus.Held, null, ReadHolder(holderPath), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DataDirectoryLockResult(DataDirectoryLockStatus.Unsupported, null, null, ex);
        }

        try
        {
            File.WriteAllText(
                holderPath,
                string.Create(CultureInfo.InvariantCulture, $"process {Environment.ProcessId} on {Environment.MachineName}, started {DateTime.UtcNow:u}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The description only helps the error message of a second server; the lock is what matters.
        }

        return new DataDirectoryLockResult(DataDirectoryLockStatus.Acquired, new DataDirectoryLock(lockFile), null, null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lockFile.Dispose();
    }

    private static bool IsLockedByAnotherHandle(IOException exception)
    {
        if (OperatingSystem.IsWindows())
        {
            // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION.
            return exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
        }

        // .NET reports a failed non-blocking flock as an IOException carrying EWOULDBLOCK, whose number differs between systems.
        return exception.HResult == (OperatingSystem.IsLinux() ? 11 : 35);
    }

    private static string? ReadHolder(string holderPath)
    {
        try
        {
            return File.ReadAllText(holderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

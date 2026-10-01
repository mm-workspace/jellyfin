namespace Jellyfin.Server.Helpers;

/// <summary>
/// The outcome of locking the data directory.
/// </summary>
internal enum DataDirectoryLockStatus
{
    /// <summary>
    /// The lock is held by this process.
    /// </summary>
    Acquired,

    /// <summary>
    /// Another process holds the lock.
    /// </summary>
    Held,

    /// <summary>
    /// The file system or the process configuration does not allow locking.
    /// </summary>
    Unsupported
}

using System;

namespace Jellyfin.Server.Helpers;

/// <summary>
/// The result of <see cref="DataDirectoryLock.TryAcquire"/>.
/// </summary>
/// <param name="Status">The outcome.</param>
/// <param name="Lock">The lock, when it was acquired.</param>
/// <param name="Holder">A description of the process holding the lock, if known.</param>
/// <param name="Error">The error that prevented locking, if any.</param>
internal sealed record DataDirectoryLockResult(DataDirectoryLockStatus Status, DataDirectoryLock? Lock, string? Holder, Exception? Error);

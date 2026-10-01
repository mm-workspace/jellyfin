using System;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// The size and modification time of a file of the live SQLite database when the snapshot was taken.
/// </summary>
/// <param name="Name">The file name, without the directory.</param>
/// <param name="Length">The size in bytes, or <c>null</c> if the file did not exist.</param>
/// <param name="LastWriteTimeUtc">The last write time, or <c>null</c> if the file did not exist.</param>
internal sealed record ImportSourceFile(string Name, long? Length, DateTime? LastWriteTimeUtc);

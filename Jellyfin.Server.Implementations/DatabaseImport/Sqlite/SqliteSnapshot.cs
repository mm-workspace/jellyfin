using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.DatabaseImport.Sqlite;

/// <summary>
/// A consistent copy of the live SQLite database, which is what gets inspected and loaded.
/// </summary>
/// <param name="Path">The path of the copy.</param>
/// <param name="Sha256">The SHA-256 of the copy, in lowercase hex.</param>
/// <param name="SourceFiles">The files of the live database after the copy was taken.</param>
internal sealed record SqliteSnapshot(string Path, string Sha256, IReadOnlyList<ImportSourceFile> SourceFiles);

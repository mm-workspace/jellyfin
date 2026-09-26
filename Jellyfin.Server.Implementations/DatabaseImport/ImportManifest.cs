using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// What preflight found in the snapshot, and what finalize checks the PostgreSQL database against.
/// </summary>
/// <param name="FormatVersion">The version of this format.</param>
/// <param name="ServerVersion">The version of the server that wrote the manifest.</param>
/// <param name="ModelFingerprint">The <see cref="ImportModel.Fingerprint"/> of the server that wrote the manifest.</param>
/// <param name="SnapshotSha256">The SHA-256 of the snapshot file, in lowercase hex.</param>
/// <param name="SourceFiles">The files of the live SQLite database when the snapshot was taken.</param>
/// <param name="Tables">The model tables of the snapshot, ordered by name.</param>
/// <param name="Warnings">The findings that did not stop preflight.</param>
internal sealed record ImportManifest(
    int FormatVersion,
    string ServerVersion,
    string ModelFingerprint,
    string SnapshotSha256,
    IReadOnlyList<ImportSourceFile> SourceFiles,
    IReadOnlyList<ImportTableSummary> Tables,
    IReadOnlyList<ImportFinding> Warnings)
{
    /// <summary>
    /// The format version this server reads and writes.
    /// </summary>
    public const int CurrentFormatVersion = 1;
}

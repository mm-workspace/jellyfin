using System;
using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Manifest type for backups internal structure.
/// </summary>
internal class BackupManifest
{
    public required Version ServerVersion { get; set; }

    public required Version BackupEngineVersion { get; set; }

    public required DateTimeOffset DateCreated { get; set; }

    public required string[] DatabaseTables { get; set; }

    public required BackupOptions Options { get; set; }

    /// <summary>
    /// Gets or sets the key of the database provider the backup was created with. Older backups do not have it.
    /// </summary>
    public string? DatabaseProvider { get; set; }

    /// <summary>
    /// Gets or sets the number of rows written per table. Older backups do not have it.
    /// </summary>
    public Dictionary<string, long>? TableRowCounts { get; set; }
}

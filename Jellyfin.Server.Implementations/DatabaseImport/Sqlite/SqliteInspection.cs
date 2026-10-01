using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Server.Implementations.DatabaseImport.Sqlite;

/// <summary>
/// The result of inspecting a SQLite source.
/// </summary>
/// <param name="Findings">The findings, errors first.</param>
/// <param name="Tables">The model tables that could be read, ordered by name.</param>
internal sealed record SqliteInspection(IReadOnlyList<ImportFinding> Findings, IReadOnlyList<ImportTableSummary> Tables)
{
    /// <summary>
    /// Gets a value indicating whether the source can be imported.
    /// </summary>
    public bool Succeeded => Findings.All(f => f.Severity != ImportFindingSeverity.Error);
}

using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.DatabaseImport.PostgreSql;

/// <summary>
/// The result of finalizing an import.
/// </summary>
/// <param name="Findings">The findings, errors first.</param>
/// <param name="Committed">Whether the transaction was committed. It is committed only without errors.</param>
internal sealed record PostgreSqlFinalization(IReadOnlyList<ImportFinding> Findings, bool Committed);

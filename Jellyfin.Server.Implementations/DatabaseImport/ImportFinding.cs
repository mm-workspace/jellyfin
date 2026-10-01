using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// The result of one check of the import. It names where the problem is, never the values of the rows.
/// </summary>
/// <param name="Check">The id of the check.</param>
/// <param name="Severity">How the finding affects the import.</param>
/// <param name="Table">The table, if the check is about one.</param>
/// <param name="Column">The column, if the check is about one.</param>
/// <param name="Index">The index or key, if the check is about one.</param>
/// <param name="Count">The number of affected rows or objects.</param>
/// <param name="PrimaryKeySamples">The primary keys of up to <see cref="MaxPrimaryKeySamples"/> affected rows.</param>
internal sealed record ImportFinding(
    string Check,
    ImportFindingSeverity Severity,
    string? Table,
    string? Column,
    string? Index,
    long Count,
    IReadOnlyList<string> PrimaryKeySamples)
{
    /// <summary>
    /// The maximum number of primary keys a finding keeps.
    /// </summary>
    public const int MaxPrimaryKeySamples = 10;

    /// <summary>
    /// Creates a finding, keeping at most <see cref="MaxPrimaryKeySamples"/> primary keys.
    /// </summary>
    /// <param name="check">The id of the check.</param>
    /// <param name="severity">How the finding affects the import.</param>
    /// <param name="count">The number of affected rows or objects.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">The column.</param>
    /// <param name="index">The index or key.</param>
    /// <param name="primaryKeySamples">The primary keys of affected rows.</param>
    /// <returns>The finding.</returns>
    public static ImportFinding Create(
        string check,
        ImportFindingSeverity severity,
        long count,
        string? table = null,
        string? column = null,
        string? index = null,
        IEnumerable<string>? primaryKeySamples = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(check);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new ImportFinding(check, severity, table, column, index, count, (primaryKeySamples ?? []).Take(MaxPrimaryKeySamples).ToArray());
    }
}

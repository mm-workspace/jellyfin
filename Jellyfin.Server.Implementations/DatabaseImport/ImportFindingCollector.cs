using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// Counts findings by check and location, keeping the first primary keys of each.
/// </summary>
internal sealed class ImportFindingCollector
{
    private readonly Dictionary<(string Check, ImportFindingSeverity Severity, string? Table, string? Column, string? Index), Entry> _entries = [];

    /// <summary>
    /// Gets a value indicating whether an error was added.
    /// </summary>
    public bool HasErrors => _entries.Keys.Any(k => k.Severity == ImportFindingSeverity.Error);

    /// <summary>
    /// Adds an occurrence of a finding.
    /// </summary>
    /// <param name="check">The id of the check.</param>
    /// <param name="severity">How the finding affects the import.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">The column.</param>
    /// <param name="index">The index or key.</param>
    /// <param name="primaryKey">The primary key of the affected row, if the finding is about a row.</param>
    public void Add(string check, ImportFindingSeverity severity, string? table = null, string? column = null, string? index = null, Func<string>? primaryKey = null)
    {
        if (!_entries.TryGetValue((check, severity, table, column, index), out var entry))
        {
            entry = new Entry();
            _entries[(check, severity, table, column, index)] = entry;
        }

        entry.Count++;
        if (primaryKey is not null && entry.PrimaryKeys.Count < ImportFinding.MaxPrimaryKeySamples)
        {
            entry.PrimaryKeys.Add(primaryKey());
        }
    }

    /// <summary>
    /// Gets the findings, errors first.
    /// </summary>
    /// <returns>The findings.</returns>
    public IReadOnlyList<ImportFinding> ToList()
    {
        return _entries
            .Select(e => ImportFinding.Create(e.Key.Check, e.Key.Severity, e.Value.Count, e.Key.Table, e.Key.Column, e.Key.Index, e.Value.PrimaryKeys))
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Check, StringComparer.Ordinal)
            .ThenBy(f => f.Table, StringComparer.Ordinal)
            .ThenBy(f => f.Column, StringComparer.Ordinal)
            .ThenBy(f => f.Index, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class Entry
    {
        public long Count { get; set; }

        public List<string> PrimaryKeys { get; } = [];
    }
}

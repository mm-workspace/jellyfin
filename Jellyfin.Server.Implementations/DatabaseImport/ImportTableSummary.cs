namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// The row count and content hash of a table of the source database.
/// </summary>
/// <param name="Name">The table name.</param>
/// <param name="RowCount">The number of rows.</param>
/// <param name="ContentHash">The <see cref="TableContentHash.Value"/> of the rows.</param>
internal sealed record ImportTableSummary(string Name, long RowCount, string ContentHash);

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// The number of values of a timestamp column that PostgreSQL holds as infinity after the import.
/// </summary>
/// <param name="Column">The column name.</param>
/// <param name="MinValue">The number of values that round to <see cref="System.DateTime.MinValue"/>.</param>
/// <param name="PastMaxValue">The number of values that round past <see cref="System.DateTime.MaxValue"/>.</param>
internal sealed record ImportTimestampSentinels(string Column, long MinValue, long PastMaxValue);

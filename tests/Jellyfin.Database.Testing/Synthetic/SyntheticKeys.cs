using System.Collections.Generic;

namespace Jellyfin.Database.Testing.Synthetic;

/// <summary>
/// The values of the columns foreign keys point at, by table, as rows are written.
/// </summary>
internal sealed class SyntheticKeys
{
    private readonly Dictionary<string, List<object?[]>> _rows = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Registers a referenced key of a table.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <param name="ordinals">The ordinals of the key columns in the table.</param>
    /// <returns>The list the table adds its key values to.</returns>
    public List<object?[]> Register(string table, int[] ordinals)
    {
        var key = Key(table, ordinals);
        if (!_rows.TryGetValue(key, out var rows))
        {
            rows = [];
            _rows[key] = rows;
        }

        return rows;
    }

    /// <summary>
    /// Gets the key values written so far.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <param name="ordinals">The ordinals of the key columns in the table.</param>
    /// <returns>The key values.</returns>
    public IReadOnlyList<object?[]> Get(string table, int[] ordinals)
        => _rows.TryGetValue(Key(table, ordinals), out var rows) ? rows : [];

    private static string Key(string table, int[] ordinals) => table + ":" + string.Join(',', ordinals);
}

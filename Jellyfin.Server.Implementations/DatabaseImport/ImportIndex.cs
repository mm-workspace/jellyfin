using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// A B-tree index of a table: an index, the primary key or a unique constraint.
/// </summary>
/// <param name="Name">The index or constraint name.</param>
/// <param name="Columns">The indexed columns, or the columns an indexed expression reads.</param>
/// <param name="IsUnique">Whether the index is unique.</param>
/// <param name="Expression">The indexed expression, for an index over an expression.</param>
internal sealed record ImportIndex(string Name, IReadOnlyList<string> Columns, bool IsUnique, string? Expression = null);

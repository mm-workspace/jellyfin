using System.Collections.Generic;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// An index of the PostgreSQL baseline over an expression, which the model cannot declare.
/// </summary>
/// <param name="Name">The index name.</param>
/// <param name="Table">The table name.</param>
/// <param name="Columns">The columns the expression reads.</param>
/// <param name="Expression">The indexed expression.</param>
internal sealed record PostgreSqlExpressionIndex(string Name, string Table, IReadOnlyList<string> Columns, string Expression)
{
    /// <summary>
    /// Gets the statement creating the index.
    /// </summary>
    public string CreateSql => $"CREATE INDEX \"{Name}\" ON \"{Table}\" ({Expression});";
}

using System;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// Writes the names of tables and columns into SQL for SQLite and PostgreSQL.
/// </summary>
internal static class SqlIdentifier
{
    /// <summary>
    /// Quotes an identifier, so it keeps its case and may contain any character.
    /// </summary>
    /// <param name="identifier">The table or column name.</param>
    /// <returns>The quoted identifier.</returns>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

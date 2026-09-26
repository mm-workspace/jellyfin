namespace Jellyfin.Server.Implementations.DatabaseImport.PostgreSql;

/// <summary>
/// A schema object of the import target as PostgreSQL describes it.
/// </summary>
/// <param name="Kind">The kind of object: table, column, constraint, index or trigger.</param>
/// <param name="Table">The table the object belongs to.</param>
/// <param name="Name">The object name.</param>
/// <param name="Definition">The definition, including whether a constraint or index is valid.</param>
internal sealed record PostgreSqlCatalogEntry(string Kind, string Table, string Name, string Definition);

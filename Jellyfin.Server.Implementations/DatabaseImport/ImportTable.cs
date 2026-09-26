using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// A table of the import model.
/// </summary>
/// <param name="Name">The table name.</param>
/// <param name="Columns">The columns, in model order.</param>
/// <param name="PrimaryKey">The primary key columns.</param>
/// <param name="Indexes">The B-tree indexes: the primary key, unique constraints and indexes, ordered by name.</param>
/// <param name="ForeignKeys">The foreign keys.</param>
/// <param name="IdentityColumns">The integer key columns whose values come from a sequence.</param>
internal sealed record ImportTable(
    string Name,
    IReadOnlyList<ImportColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ImportIndex> Indexes,
    IReadOnlyList<ImportForeignKey> ForeignKeys,
    IReadOnlyList<string> IdentityColumns)
{
    /// <summary>
    /// Creates the description of a relational table.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <returns>The description.</returns>
    public static ImportTable Create(ITable table)
    {
        var columns = table.Columns.Select(ImportColumn.Create).ToArray();
        var primaryKey = table.PrimaryKey?.Columns.Select(c => c.Name).ToArray() ?? [];
        var indexes = table.Indexes.Select(i => new ImportIndex(i.Name, i.Columns.Select(c => c.Name).ToArray(), i.IsUnique))
            .Concat(table.UniqueConstraints.Select(u => new ImportIndex(u.Name, u.Columns.Select(c => c.Name).ToArray(), true)))
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToArray();
        var foreignKeys = table.ForeignKeyConstraints
            .Select(f => new ImportForeignKey(f.Name, f.Columns.Select(c => c.Name).ToArray(), f.PrincipalTable.Name, f.PrincipalColumns.Select(c => c.Name).ToArray(), f.OnDeleteAction))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();
        var identityColumns = table.Columns
            .Where(c => c.PropertyMappings.Any(m => m.Property.ValueGenerated == ValueGenerated.OnAdd && m.Property.IsKey() && (m.Property.ClrType == typeof(int) || m.Property.ClrType == typeof(long))))
            .Select(c => c.Name)
            .ToArray();
        return new ImportTable(table.Name, columns, primaryKey, indexes, foreignKeys, identityColumns);
    }
}

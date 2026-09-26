using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// The tables and identity columns of the Jellyfin model as they exist in PostgreSQL.
/// </summary>
internal sealed class PostgreSqlModelCatalog
{
    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    private readonly Dictionary<string, ITable> _tables;

    private PostgreSqlModelCatalog(IModel model)
    {
        _tables = model.GetRelationalModel().Tables
            .Where(t => !t.Name.Equals(MigrationsHistoryTable, StringComparison.Ordinal))
            .ToDictionary(t => t.SchemaQualifiedName, StringComparer.Ordinal);

        IdentityColumns = _tables.Values
            .SelectMany(t => t.Columns.Select(c => (Table: t, Column: c)))
            .Where(e => e.Column.PropertyMappings.Any(m => IsIdentity(m.Property)))
            .Select(e => new IdentityColumn(e.Table.Name, e.Table.Schema, e.Column.Name))
            .OrderBy(e => e.Table, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Gets the tables of the model, keyed by their schema qualified name.
    /// </summary>
    public IReadOnlyDictionary<string, ITable> Tables => _tables;

    /// <summary>
    /// Gets the integer key columns whose values come from an identity sequence.
    /// </summary>
    public IReadOnlyList<IdentityColumn> IdentityColumns { get; }

    /// <summary>
    /// Creates the catalog of the model a context uses.
    /// </summary>
    /// <param name="model">The design time model of the context.</param>
    /// <returns>The catalog.</returns>
    public static PostgreSqlModelCatalog Create(IModel model) => new(model);

    /// <summary>
    /// Adds every table that references one of the given tables through a foreign key, until no table is missing.
    /// </summary>
    /// <param name="tables">The tables to start from.</param>
    /// <returns>The tables and all tables depending on them.</returns>
    public IReadOnlyCollection<ITable> WithDependents(IEnumerable<ITable> tables)
    {
        var result = new HashSet<ITable>(tables);
        var added = true;
        while (added)
        {
            added = false;
            foreach (var table in _tables.Values)
            {
                if (!result.Contains(table) && table.ForeignKeyConstraints.Any(fk => result.Contains(fk.PrincipalTable)))
                {
                    result.Add(table);
                    added = true;
                }
            }
        }

        return result;
    }

    private static bool IsIdentity(IProperty property)
        => property.ClrType == typeof(int) || property.ClrType == typeof(long)
            ? property.GetValueGenerationStrategy() is NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                or NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
                or NpgsqlValueGenerationStrategy.SerialColumn
            : false;

    /// <summary>
    /// An identity column.
    /// </summary>
    /// <param name="Table">The table name.</param>
    /// <param name="Schema">The schema, or <c>null</c> for the default schema.</param>
    /// <param name="Column">The column name.</param>
    internal sealed record IdentityColumn(string Table, string? Schema, string Column);
}

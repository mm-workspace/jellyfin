using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Schema objects of the PostgreSQL baseline that cannot be declared on the model.
/// </summary>
internal static class PostgreSqlBaselineSql
{
    /// <summary>
    /// Gets the expression indexes of the baseline.
    /// </summary>
    public static IReadOnlyList<PostgreSqlExpressionIndex> ExpressionIndexes { get; } =
    [
        // The column uses the binary collation, so the index matches the lower("Name") expression the people queries use.
        new("IX_Peoples_NameLower", "Peoples", ["Name"], "lower(\"Name\")"),

        // Alternate versions are matched to their group through COALESCE(PrimaryVersionId, Id); without an index
        // PostgreSQL recomputes that lookup for every item of the resume query (10 s instead of 0.2 s on 50 000 items).
        new("IX_BaseItems_VersionGroup", "BaseItems", ["PrimaryVersionId", "Id"], "COALESCE(\"PrimaryVersionId\", \"Id\")")
    ];

    /// <summary>
    /// Adds the objects to the baseline migration.
    /// </summary>
    /// <param name="migrationBuilder">The migration builder of the baseline.</param>
    public static void Apply(MigrationBuilder migrationBuilder)
    {
        foreach (var index in ExpressionIndexes)
        {
            migrationBuilder.Sql(index.CreateSql);
        }
    }
}

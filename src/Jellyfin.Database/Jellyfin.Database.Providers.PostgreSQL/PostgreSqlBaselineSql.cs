using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Schema objects of the PostgreSQL baseline that cannot be declared on the model.
/// </summary>
internal static class PostgreSqlBaselineSql
{
    /// <summary>
    /// Adds the objects to the baseline migration.
    /// </summary>
    /// <param name="migrationBuilder">The migration builder of the baseline.</param>
    public static void Apply(MigrationBuilder migrationBuilder)
    {
        // Expression index, so it cannot be declared on the entity type. The column uses the binary collation, so
        // the index matches the lower("Name") expression the people queries use.
        migrationBuilder.Sql("CREATE INDEX \"IX_Peoples_NameLower\" ON \"Peoples\" (lower(\"Name\"));");
    }
}

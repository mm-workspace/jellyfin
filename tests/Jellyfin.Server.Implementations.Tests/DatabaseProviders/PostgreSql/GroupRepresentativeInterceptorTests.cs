using Jellyfin.Database.Providers.PostgreSQL.Query;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

public class GroupRepresentativeInterceptorTests
{
    [Fact]
    public void Rewrite_GroupRepresentativeWithFallback_KeepsItAsSubPlan()
    {
        const string Sql = """
            SELECT b."Id"
            FROM "BaseItems" AS b
            WHERE b."Id" IN (
                SELECT COALESCE(MIN(CASE
                    WHEN b0."PrimaryVersionId" IS NULL THEN b0."Id"::text COLLATE "C"
                END)::uuid, MIN(b0."Id"::text COLLATE "C")::uuid)
                FROM "BaseItems" AS b0
                WHERE b0."Name" = 'a (b)' AND b0."Type" = @p
                GROUP BY b0."PresentationUniqueKey"
            ) AND b."IsFolder"
            """;

        var rewritten = GroupRepresentativeInterceptor.Rewrite(Sql);

        Assert.Equal(Sql.Replace(") AND b.\"IsFolder\"", ") IS TRUE AND b.\"IsFolder\"", System.StringComparison.Ordinal), rewritten);
    }

    [Fact]
    public void Rewrite_SingleAggregate_KeepsItAsSubPlan()
    {
        const string Sql = """
            SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (
                SELECT MIN(b0."Id"::text COLLATE "C")::uuid
                FROM "BaseItems" AS b0
                GROUP BY b0."SeriesPresentationUniqueKey"
            )
            """;

        Assert.Equal(Sql + " IS TRUE", GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Theory]
    [InlineData("""SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT u."ItemId" FROM "UserData" AS u WHERE u."Name" = 'x COLLATE "C")::uuid')""")]
    [InlineData("""SELECT 1 FROM "BaseItems" AS b WHERE b."Id" NOT IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 GROUP BY b0."Name")""")]
    [InlineData("""SELECT 1 FROM "BaseItems" AS b WHERE b."Name" IN (SELECT b0."Name" FROM "BaseItems" AS b0 WHERE b0."Id" = (SELECT MIN(b1."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b1))""")]
    [InlineData("""SELECT b."Id" FROM "BaseItems" AS b""")]
    public void Rewrite_OtherStatements_AreUnchanged(string sql)
    {
        Assert.Same(sql, GroupRepresentativeInterceptor.Rewrite(sql));
    }
}

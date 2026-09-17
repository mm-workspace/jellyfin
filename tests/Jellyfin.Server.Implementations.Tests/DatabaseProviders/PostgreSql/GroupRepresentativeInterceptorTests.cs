using Jellyfin.Database.Providers.PostgreSQL.Query;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

public class GroupRepresentativeInterceptorTests
{
    [Fact]
    public void Rewrite_RepresentativesOfOneSeries_MovesThemIntoACommonTableExpression()
    {
        const string Sql = """
            SELECT b."Id"
            FROM "BaseItems" AS b
            WHERE b."Id" IN (
                SELECT COALESCE(MIN(CASE
                    WHEN b0."PrimaryVersionId" IS NULL THEN b0."Id"::text COLLATE "C"
                END)::uuid, MIN(b0."Id"::text COLLATE "C")::uuid)
                FROM "BaseItems" AS b0
                WHERE b0."SeriesPresentationUniqueKey" = @p AND b0."Name" = 'a (b)'
                GROUP BY b0."PresentationUniqueKey"
            ) AND b."IsFolder"
            """;
        const string Expected = """
            WITH "__GroupRepresentatives1" AS MATERIALIZED (
                SELECT COALESCE(MIN(CASE
                    WHEN b0."PrimaryVersionId" IS NULL THEN b0."Id"::text COLLATE "C"
                END)::uuid, MIN(b0."Id"::text COLLATE "C")::uuid)
                FROM "BaseItems" AS b0
                WHERE b0."SeriesPresentationUniqueKey" = @p AND b0."Name" = 'a (b)'
                GROUP BY b0."PresentationUniqueKey"
            )
            SELECT b."Id"
            FROM "BaseItems" AS b
            WHERE b."Id" IN (SELECT * FROM "__GroupRepresentatives1") AND b."IsFolder"
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Fact]
    public void Rewrite_RepresentativesOfALibrary_KeepsThemAsSubPlan()
    {
        const string Sql = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."TopParentId" = @p GROUP BY b0."PresentationUniqueKey")
            """;

        Assert.Equal(Sql + " IS TRUE", GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Fact]
    public void Rewrite_ColumnComparedWithAColumn_KeepsThemAsSubPlan()
    {
        // The key is compared with the outer item's key, so the subquery is not restricted to one group.
        const string Sql = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = b1."PresentationUniqueKey" GROUP BY b0."PresentationUniqueKey")
            """;

        Assert.Equal(Sql + " IS TRUE", GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Fact]
    public void Rewrite_SeveralGroupRepresentatives_RewritesEachOnItsOwn()
    {
        const string Sql = """
            SELECT count(*) FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = ')' GROUP BY b0."Name") OR b."ParentId" IN (SELECT MIN(b1."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b1 WHERE b1."TopParentId" = @p GROUP BY b1."Type")
            """;
        const string Expected = """
            WITH "__GroupRepresentatives1" AS MATERIALIZED (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = ')' GROUP BY b0."Name")
            SELECT count(*) FROM "BaseItems" AS b WHERE b."Id" IN (SELECT * FROM "__GroupRepresentatives1") OR b."ParentId" IN (SELECT MIN(b1."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b1 WHERE b1."TopParentId" = @p GROUP BY b1."Type") IS TRUE
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Theory]
    [InlineData("""WITH x AS (SELECT 1 AS n) SELECT 1 FROM "BaseItems" AS b, x WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p GROUP BY b0."Name")""")]
    [InlineData("""SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p GROUP BY b0."Name"); SELECT 2""")]
    public void Rewrite_StatementWithoutRoomForACommonTableExpression_KeepsThemAsSubPlan(string sql)
    {
        var expected = sql.Replace("GROUP BY b0.\"Name\")", "GROUP BY b0.\"Name\") IS TRUE", System.StringComparison.Ordinal);

        Assert.Equal(expected, GroupRepresentativeInterceptor.Rewrite(sql));
    }

    [Fact]
    public void Rewrite_StatementEndingInASemicolon_MovesThemIntoACommonTableExpression()
    {
        const string Sql = """
            SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p GROUP BY b0."Name");
            """;
        const string Expected = """
            WITH "__GroupRepresentatives1" AS MATERIALIZED (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p GROUP BY b0."Name")
            SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT * FROM "__GroupRepresentatives1");
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Theory]
    [InlineData("""SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT u."ItemId" FROM "UserData" AS u WHERE u."Name" = 'x COLLATE "C")::uuid')""")]
    [InlineData("""SELECT 1 FROM "BaseItems" AS b WHERE b."Id" NOT IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 GROUP BY b0."Name")""")]
    [InlineData("""SELECT 1 FROM "BaseItems" AS b WHERE b."Name" IN (SELECT b0."Name" FROM "BaseItems" AS b0 WHERE b0."Id" = (SELECT MIN(b1."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b1))""")]
    [InlineData("""SELECT 1 FROM "BaseItems" AS b WHERE b."Name" = ' IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 GROUP BY b0."Name")'""")]
    [InlineData("""SELECT b."Id" FROM "BaseItems" AS b""")]
    public void Rewrite_OtherStatements_AreUnchanged(string sql)
    {
        Assert.Same(sql, GroupRepresentativeInterceptor.Rewrite(sql));
    }
}

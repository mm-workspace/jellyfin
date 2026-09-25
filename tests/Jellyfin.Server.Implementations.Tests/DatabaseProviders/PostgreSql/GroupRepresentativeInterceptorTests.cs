using System;
using System.Linq;
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
                SELECT DISTINCT ON (b0."PresentationUniqueKey") b0."Id" FROM "BaseItems" AS b0
                WHERE b0."SeriesPresentationUniqueKey" = @p AND b0."Name" = 'a (b)'
                ORDER BY b0."PresentationUniqueKey", (b0."PrimaryVersionId" IS NULL) IS NOT TRUE, b0."Id"
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
        const string Expected = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT DISTINCT ON (b0."PresentationUniqueKey") b0."Id" FROM "BaseItems" AS b0 WHERE b0."TopParentId" = @p ORDER BY b0."PresentationUniqueKey", b0."Id") IS TRUE
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Fact]
    public void Rewrite_ColumnComparedWithAColumn_KeepsThemAsSubPlan()
    {
        // The key is compared with the outer item's key, so the subquery is not restricted to one group.
        const string Sql = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = b1."PresentationUniqueKey" GROUP BY b0."PresentationUniqueKey")
            """;
        const string Expected = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT DISTINCT ON (b0."PresentationUniqueKey") b0."Id" FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = b1."PresentationUniqueKey" ORDER BY b0."PresentationUniqueKey", b0."Id") IS TRUE
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Fact]
    public void Rewrite_RepresentativesOfOneSeriesReferringToTheOuterItem_KeepsThemAsSubPlan()
    {
        // A common table expression cannot see b, so moving the subquery there would break the statement.
        const string Sql = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."SeriesPresentationUniqueKey" = @p AND b0."ParentId" = b."ParentId" GROUP BY b0."PresentationUniqueKey")
            """;
        const string Expected = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT DISTINCT ON (b0."PresentationUniqueKey") b0."Id" FROM "BaseItems" AS b0 WHERE b0."SeriesPresentationUniqueKey" = @p AND b0."ParentId" = b."ParentId" ORDER BY b0."PresentationUniqueKey", b0."Id") IS TRUE
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Theory]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."ParentId" = "b"."ParentId" AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE EXISTS (SELECT 1 FROM "UserData" AS u WHERE u."ItemId" = b."Id") AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."ParentId" = b . "ParentId" AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."ParentId" = p.value AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid AS b FROM "BaseItems" AS b0 WHERE b0."ParentId" = b."ParentId" AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."Name" <> 'FROM "BaseItems" AS b' AND b0."ParentId" = b."ParentId" AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE NOT EXISTS (SELECT 1 FROM "BaseItems" AS "B" WHERE "B"."Id" = b."ParentId") AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE EXISTS (SELECT b1."Name" IS DISTINCT FROM lower(b1."SortName") AS b FROM "BaseItems" AS b1) AND b0."ParentId" = b."ParentId" AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE EXISTS (SELECT b1."Name" IS NOT DISTINCT FROM lower(b1."SortName") AS b FROM "BaseItems" AS b1) AND b0."ParentId" = b."ParentId" AND b0."SeriesPresentationUniqueKey" = @p""")]
    public void Rewrite_RepresentativesReferringToTheOuterStatement_KeepsThemAsSubPlan(string ungrouped)
    {
        // Quoted or not, directly or from a nested subquery: b and p belong to the enclosing statement. Neither a
        // column alias, nor a string literal, nor a quoted name that differs in case declares them in the subquery.
        // Only where the representatives end up is at stake here; how they are selected is tested on its own below.
        var sql = $"""SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN ({ungrouped} GROUP BY b0."PresentationUniqueKey")""";
        var rewritten = GroupRepresentativeInterceptor.Rewrite(sql);

        Assert.EndsWith(") IS TRUE", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("__GroupRepresentatives", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE (b0."PrimaryVersionId" IS NULL OR NOT EXISTS (SELECT 1 FROM "BaseItems" AS b1 WHERE b1."Id" = b0."PrimaryVersionId" AND b1."TopParentId" = b0."TopParentId")) AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 LEFT JOIN "UserData" AS u ON b0."Id" = u."ItemId" CROSS JOIN LATERAL (SELECT 1 AS n) AS s WHERE u."Played" AND s.n = 1 AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."Name" <> 'b."Id" = x."Id" isn''t a reference' AND b0."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN("b0"."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS "b0" WHERE "b0"."SeriesPresentationUniqueKey" = @p""")]
    [InlineData("""SELECT MIN("b0"."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS B0 WHERE "b0"."SeriesPresentationUniqueKey" = @p""")]
    public void Rewrite_RepresentativesReferringToTheirOwnTables_MovesThemIntoACommonTableExpression(string ungrouped)
    {
        // Nested subqueries, joins, string literals and quoted names do not hide an alias declared in the subquery.
        var sql = $"""SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN ({ungrouped} GROUP BY b0."PresentationUniqueKey")""";
        var rewritten = GroupRepresentativeInterceptor.Rewrite(sql);

        Assert.StartsWith("""WITH "__GroupRepresentatives1" AS MATERIALIZED (SELECT DISTINCT ON (b0."PresentationUniqueKey")""", rewritten, StringComparison.Ordinal);
        Assert.EndsWith("""SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT * FROM "__GroupRepresentatives1")""", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_RepresentativesDeclaringManyTables_KeepsThemAsSubPlan()
    {
        // Past a limit the declared aliases are not tracked, so whether the subquery stands on its own is not known.
        var joins = string.Concat(Enumerable.Range(1, 40).Select(i => $""" JOIN "UserData" AS u{i} ON u{i}."ItemId" = b0."Id" """));
        var sql = $"""SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0{joins}WHERE b0."SeriesPresentationUniqueKey" = @p GROUP BY b0."PresentationUniqueKey")""";
        var rewritten = GroupRepresentativeInterceptor.Rewrite(sql);

        Assert.EndsWith(") IS TRUE", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("__GroupRepresentatives", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_SeveralGroupRepresentatives_RewritesEachOnItsOwn()
    {
        const string Sql = """
            SELECT count(*) FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = ')' GROUP BY b0."Name") OR b."ParentId" IN (SELECT MIN(b1."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b1 WHERE b1."TopParentId" = @p GROUP BY b1."Type")
            """;
        const string Expected = """
            WITH "__GroupRepresentatives1" AS MATERIALIZED (SELECT DISTINCT ON (b0."Name") b0."Id" FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = ')' ORDER BY b0."Name", b0."Id")
            SELECT count(*) FROM "BaseItems" AS b WHERE b."Id" IN (SELECT * FROM "__GroupRepresentatives1") OR b."ParentId" IN (SELECT DISTINCT ON (b1."Type") b1."Id" FROM "BaseItems" AS b1 WHERE b1."TopParentId" = @p ORDER BY b1."Type", b1."Id") IS TRUE
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Theory]
    [InlineData(
        """WITH x AS (SELECT 1 AS n) SELECT 1 FROM "BaseItems" AS b, x WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p GROUP BY b0."Name")""",
        """WITH x AS (SELECT 1 AS n) SELECT 1 FROM "BaseItems" AS b, x WHERE b."Id" IN (SELECT DISTINCT ON (b0."Name") b0."Id" FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p ORDER BY b0."Name", b0."Id") IS TRUE""")]
    [InlineData(
        """SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p GROUP BY b0."Name"); SELECT 2""",
        """SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT DISTINCT ON (b0."Name") b0."Id" FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p ORDER BY b0."Name", b0."Id") IS TRUE; SELECT 2""")]
    public void Rewrite_StatementWithoutRoomForACommonTableExpression_KeepsThemAsSubPlan(string sql, string expected)
    {
        Assert.Equal(expected, GroupRepresentativeInterceptor.Rewrite(sql));
    }

    [Fact]
    public void Rewrite_StatementEndingInASemicolon_MovesThemIntoACommonTableExpression()
    {
        const string Sql = """
            SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p GROUP BY b0."Name");
            """;
        const string Expected = """
            WITH "__GroupRepresentatives1" AS MATERIALIZED (SELECT DISTINCT ON (b0."Name") b0."Id" FROM "BaseItems" AS b0 WHERE b0."PresentationUniqueKey" = @p ORDER BY b0."Name", b0."Id")
            SELECT 1 FROM "BaseItems" AS b WHERE b."Id" IN (SELECT * FROM "__GroupRepresentatives1");
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Fact]
    public void Rewrite_RepresentativesOfSeveralKeys_OrdersOnAllOfThem()
    {
        const string Sql = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT COALESCE(MIN(CASE WHEN b0."PrimaryVersionId" IS NULL THEN b0."Id"::text COLLATE "C" END)::uuid, MIN(b0."Id"::text COLLATE "C")::uuid) FROM "BaseItems" AS b0 WHERE b0."TopParentId" = @p GROUP BY b0."PresentationUniqueKey", b0."SeriesPresentationUniqueKey")
            """;
        const string Expected = """
            SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN (SELECT DISTINCT ON (b0."PresentationUniqueKey", b0."SeriesPresentationUniqueKey") b0."Id" FROM "BaseItems" AS b0 WHERE b0."TopParentId" = @p ORDER BY b0."PresentationUniqueKey", b0."SeriesPresentationUniqueKey", (b0."PrimaryVersionId" IS NULL) IS NOT TRUE, b0."Id") IS TRUE
            """;

        Assert.Equal(Expected, GroupRepresentativeInterceptor.Rewrite(Sql));
    }

    [Theory]
    [InlineData("""
        SELECT MIN(b0."Id"::text COLLATE "C")::uuid AS x FROM "BaseItems" AS b0 WHERE b0."TopParentId" = @p GROUP BY b0."Name"
        """)]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."TopParentId" = @p GROUP BY b0."Name" HAVING count(*) > 1""")]
    [InlineData("""
        SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."TopParentId" = @p GROUP BY b0."Name" ORDER BY b0."Name"
        """)]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."TopParentId" = @p GROUP BY b0."Name" LIMIT 1""")]
    [InlineData("""SELECT MIN(b0."Id"::text COLLATE "C")::uuid FROM "BaseItems" AS b0 WHERE b0."Name" = 'GROUP BY x'""")]
    [InlineData("""
        SELECT COALESCE(MIN(CASE WHEN b0."PrimaryVersionId" IS NULL THEN b0."Id"::text COLLATE "C" END)::uuid, MIN(b0."ParentId"::text COLLATE "C")::uuid) FROM "BaseItems" AS b0 GROUP BY b0."Name"
        """)]
    [InlineData("""
        SELECT COALESCE(MIN(CASE WHEN b0."PrimaryVersionId" IS NULL THEN b0."Id"::text COLLATE "C" ELSE b0."ParentId"::text COLLATE "C" END)::uuid, MIN(b0."Id"::text COLLATE "C")::uuid) FROM "BaseItems" AS b0 GROUP BY b0."Name"
        """)]
    [InlineData("""
        SELECT COALESCE(MIN(CASE WHEN b0."PrimaryVersionId" IS NULL THEN b0."Id"::text COLLATE "C" WHEN b0."ParentId" IS NULL THEN b0."ParentId"::text COLLATE "C" END)::uuid, MIN(b0."Id"::text COLLATE "C")::uuid) FROM "BaseItems" AS b0 GROUP BY b0."Name"
        """)]
    public void Rewrite_RepresentativesOfAnotherShape_KeepTheirAggregate(string representatives)
    {
        // Anything that does not read as one value per group of one expression is left as it is: a projection that
        // goes on past the aggregate, a clause after the grouping keys, a second branch, a second expression.
        var sql = $"""SELECT b."Id" FROM "BaseItems" AS b WHERE b."Id" IN ({representatives})""";

        Assert.Equal(sql + " IS TRUE", GroupRepresentativeInterceptor.Rewrite(sql));
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

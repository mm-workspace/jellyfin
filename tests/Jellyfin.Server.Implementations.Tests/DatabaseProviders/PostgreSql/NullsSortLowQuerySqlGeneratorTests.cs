using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

/// <summary>
/// The SQL the provider writes for an ordering. Nothing here opens a connection, so no server is needed.
/// </summary>
public sealed class NullsSortLowQuerySqlGeneratorTests : IDisposable
{
    // The only sort key whose first ordering reads a column the model marks as not nullable.
    private static readonly ItemSortBy[] _sortKeysThatCannotBeNull = [ItemSortBy.IsFolder];

    private readonly JellyfinDbContext _context;

    public NullsSortLowQuerySqlGeneratorTests()
    {
        var provider = new PostgreSqlDatabaseProvider(null!, NullLogger<PostgreSqlDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(builder, new DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-PostgreSQL",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,

                // Never connected to: the tests only ask for the SQL of a query.
                ConnectionString = "Host=127.0.0.1;Port=1;Database=jellyfin;Username=jellyfin"
            }
        });

        _context = new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    public static TheoryData<ItemSortBy, SortOrder> SortKeysThatCanBeNull
        => Directions(Enum.GetValues<ItemSortBy>().Except(_sortKeysThatCannotBeNull).Where(s => s != ItemSortBy.Default));

    public static TheoryData<ItemSortBy, SortOrder> SortKeysThatCannotBeNull => Directions(_sortKeysThatCannotBeNull);

    [Fact]
    public void Ordering_NullableColumn_SortsNullFirstAscendingAndLastDescending()
    {
        Assert.EndsWith("ORDER BY b.\"SortName\" NULLS FIRST", OrderBySql(_context.BaseItems.OrderBy(e => e.SortName)), StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY b.\"SortName\" DESC NULLS LAST", OrderBySql(_context.BaseItems.OrderByDescending(e => e.SortName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Ordering_ColumnThatCannotBeNull_IsLeftToPostgreSql()
    {
        Assert.EndsWith("ORDER BY b.\"Id\"", OrderBySql(_context.BaseItems.OrderBy(e => e.Id)), StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY b.\"Type\", b.\"IsFolder\" DESC", OrderBySql(_context.BaseItems.OrderBy(e => e.Type).ThenByDescending(e => e.IsFolder)), StringComparison.Ordinal);
    }

    [Fact]
    public void Ordering_NoLongerOrdersOnWhetherTheKeyIsNull()
    {
        var sql = _context.BaseItems.OrderByDescending(e => e.ProductionYear).ThenBy(e => e.SortName).ToQueryString();

        Assert.DoesNotContain("CASE", sql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY b.\"ProductionYear\" DESC NULLS LAST, b.\"SortName\" NULLS FIRST", OrderBySql(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void Ordering_ColumnOfAnOptionalNavigation_SortsNullLow()
    {
        // The column is not nullable in its own table, but the row it is read from may be missing.
        Assert.EndsWith(" NULLS FIRST", OrderBySql(_context.BaseItems.OrderBy(e => e.DirectParent!.IsFolder)), StringComparison.Ordinal);
        Assert.EndsWith(" NULLS LAST", OrderBySql(_context.BaseItems.OrderByDescending(e => e.Owner!.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public void Ordering_KeyReadThroughASubquery_SortsNullLow()
    {
        Assert.EndsWith(" NULLS FIRST", OrderBySql(_context.BaseItems.OrderBy(e => e.UserData!.FirstOrDefault()!.PlayCount)), StringComparison.Ordinal);
        Assert.EndsWith(" NULLS LAST", OrderBySql(_context.BaseItems.OrderByDescending(e => e.UserData!.Max(u => u.PlayCount))), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SortKeysThatCanBeNull))]
    public void ApplyOrder_SortKeyThatCanBeNull_SortsNullLow(ItemSortBy sortBy, SortOrder sortOrder)
    {
        Assert.Contains(
            sortOrder == SortOrder.Ascending ? " NULLS FIRST" : " NULLS LAST",
            FirstOrdering(sortBy, sortOrder),
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SortKeysThatCannotBeNull))]
    public void ApplyOrder_SortKeyThatCannotBeNull_IsLeftToPostgreSql(ItemSortBy sortBy, SortOrder sortOrder)
    {
        Assert.DoesNotContain("NULLS", FirstOrdering(sortBy, sortOrder), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SortOrder.Ascending)]
    [InlineData(SortOrder.Descending)]
    public void ApplyOrder_NoSortKey_SortsOnTheSortNameAscendingWithNullFirst(SortOrder sortOrder)
    {
        // The repository drops the default sort key, whichever direction it was asked for, and falls back to the sort name.
        Assert.Equal("b.\"SortName\" NULLS FIRST", FirstOrdering(ItemSortBy.Default, sortOrder));
    }

    public void Dispose() => _context.Dispose();

    private static TheoryData<ItemSortBy, SortOrder> Directions(IEnumerable<ItemSortBy> sortKeys)
    {
        var data = new TheoryData<ItemSortBy, SortOrder>();
        foreach (var sortBy in sortKeys)
        {
            data.Add(sortBy, SortOrder.Ascending);
            data.Add(sortBy, SortOrder.Descending);
        }

        return data;
    }

    private static string OrderBySql<T>(IQueryable<T> query) => OrderBySql(query.ToQueryString());

    // The last ORDER BY of a statement, which is the one the rows come back in.
    private static string OrderBySql(string sql) => sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..].TrimEnd();

    // The first ordering of the ORDER BY the repository builds for a sort key, which is the one that decides the order.
    private string FirstOrdering(ItemSortBy sortBy, SortOrder sortOrder)
    {
        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        var repository = new BaseItemRepository(
            Mock.Of<IDbContextFactory<JellyfinDbContext>>(),
            Mock.Of<IServerApplicationHost>(),
            new ItemTypeLookup(),
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);
        var filter = new InternalItemsQuery(new User("test", "auth-provider", "reset-provider")) { OrderBy = [(sortBy, sortOrder)] };

        var orderBy = OrderBySql(repository.ApplyOrder(_context.BaseItems, filter, _context))["ORDER BY ".Length..];

        // The orderings are separated by commas, which the keys themselves can hold inside brackets.
        var depth = 0;
        for (var i = 0; i < orderBy.Length; i++)
        {
            depth += orderBy[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0 && orderBy[i] == ',')
            {
                return orderBy[..i];
            }
        }

        return orderBy;
    }
}

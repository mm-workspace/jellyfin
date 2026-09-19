using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Providers.PostgreSQL.Query;
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

public sealed class NullsSortLowInterceptorTests : IDisposable
{
    // A random number, a required column, a COALESCE and the played predicate.
    private static readonly ItemSortBy[] _sortKeysThatCannotBeNull = [ItemSortBy.Random, ItemSortBy.IsFolder, ItemSortBy.IsFavoriteOrLiked, ItemSortBy.IsPlayed, ItemSortBy.IsUnplayed];

    private readonly JellyfinDbContext _context = new PostgreSqlDesignTimeJellyfinDbFactory().CreateDbContext([]);

    public static TheoryData<ItemSortBy> SortKeysThatCannotBeNull => new(_sortKeysThatCannotBeNull);

    public static TheoryData<ItemSortBy> SortKeysThatCanBeNull => new(Enum.GetValues<ItemSortBy>().Except(_sortKeysThatCannotBeNull));

    [Fact]
    public void Rewrite_NullableKey_OrdersOnWhetherItIsNullFirst()
    {
        var orderings = Orderings(Rewrite(_context.BaseItems.OrderByDescending(e => e.ProductionYear)));

        Assert.Equal([nameof(Queryable.OrderByDescending), nameof(Queryable.ThenByDescending)], orderings.Select(o => o.Method));
        Assert.Equal("e => IIF((e.ProductionYear == null), 0, 1)", orderings[0].Key.ToString());
        Assert.Equal("e => e.ProductionYear", orderings[1].Key.ToString());
    }

    [Fact]
    public void Rewrite_ValueTypeKeyThatCanBeNull_ComparesItsNullableFormWithNull()
    {
        var orderings = Orderings(Rewrite(_context.BaseItems.OrderBy(e => e.UserData!.FirstOrDefault()!.PlayCount)));

        Assert.Equal(2, orderings.Count);
        var nullTest = Assert.IsAssignableFrom<BinaryExpression>(Assert.IsAssignableFrom<ConditionalExpression>(orderings[0].Key.Body).Test);
        Assert.Equal(typeof(int?), nullTest.Left.Type);
    }

    [Fact]
    public void Rewrite_MemberOfElementOperator_AddsNullOrdering()
    {
        Assert.True(AddsNullOrdering(e => e.UserData!.FirstOrDefault()!.PlayCount));
        Assert.True(AddsNullOrdering(e => e.UserData!.SingleOrDefault()!.PlayCount));
        Assert.True(AddsNullOrdering(e => e.UserData!.OrderBy(u => u.CustomDataKey).LastOrDefault()!.PlayCount));
        Assert.True(AddsNullOrdering(e => e.UserData!.OrderBy(u => u.CustomDataKey).ElementAtOrDefault(1)!.PlayCount));
        Assert.True(AddsNullOrdering(e => e.UserData!.First().PlayCount));
        Assert.True(AddsNullOrdering(e => _context.UserData.Where(u => u.ItemId.Equals(e.Id)).FirstOrDefault()!.Played));
        Assert.True(AddsNullOrdering(e => e.UserData!.Select(u => u.PlayCount).FirstOrDefault()));
        Assert.True(AddsNullOrdering(e => e.UserData!.FirstOrDefault()!.Item!.IsFolder));
    }

    [Fact]
    public void Rewrite_MaxOrMinOfPossiblyEmptySet_AddsNullOrdering()
    {
        Assert.True(AddsNullOrdering(e => e.UserData!.Max(u => u.PlayCount)));
        Assert.True(AddsNullOrdering(e => e.UserData!.Select(u => u.PlaybackPositionTicks).Min()));
        Assert.True(AddsNullOrdering(e => _context.UserData.Where(u => u.ItemId.Equals(e.Id)).Max(u => u.PlayCount)));
    }

    [Fact]
    public void Rewrite_AggregateOfGroup_AddsNullOrderingOnlyForNullableValues()
    {
        var groups = _context.BaseItems.GroupBy(e => e.Type);

        Assert.Single(Orderings(Rewrite(groups.OrderBy(g => g.Max(e => e.IsFolder ? 1 : 0)))));
        Assert.Equal(2, Orderings(Rewrite(groups.OrderBy(g => g.Max(e => e.ProductionYear)))).Count);
    }

    [Fact]
    public void Rewrite_MemberOfOptionalNavigation_AddsNullOrdering()
    {
        Assert.True(AddsNullOrdering(e => e.DirectParent!.IsFolder));
        Assert.True(AddsNullOrdering(e => e.Owner!.Id));
    }

    [Fact]
    public void Rewrite_MemberOfRequiredNavigation_IsLeftAlone()
    {
        Assert.Single(Orderings(Rewrite(_context.UserData.OrderBy(u => u.Item!.IsFolder))));
        Assert.Single(Orderings(Rewrite(_context.UserData.OrderBy(u => u.User!.Id))));
    }

    [Fact]
    public void Rewrite_OperatorOnValueThatCanBeNull_AddsNullOrdering()
    {
        Assert.True(AddsNullOrdering(e => !e.UserData!.FirstOrDefault()!.Played));
        Assert.True(AddsNullOrdering(e => -e.UserData!.FirstOrDefault()!.PlayCount));
        Assert.True(AddsNullOrdering(e => e.UserData!.FirstOrDefault()!.PlayCount + 1));
        Assert.True(AddsNullOrdering(e => e.IsFolder || e.UserData!.FirstOrDefault()!.Played));
        Assert.True(AddsNullOrdering(e => e.IsFolder ? 0 : e.UserData!.FirstOrDefault()!.PlayCount));
        Assert.True(AddsNullOrdering(e => e.IsFolder ? 0 : e.ProductionYear));
        Assert.True(AddsNullOrdering(e => e.ProductionYear!.Value));
    }

    [Fact]
    public void Rewrite_KeyThatCannotBeNull_IsLeftAlone()
    {
        Assert.False(AddsNullOrdering(e => e.Id));
        Assert.False(AddsNullOrdering(e => (object)e.IsFolder));
        Assert.False(AddsNullOrdering(e => e.IsFolder ? 1 : 0));
        Assert.False(AddsNullOrdering(e => e.ProductionYear ?? 0));
        Assert.False(AddsNullOrdering(e => e.UserData!.Count));
        Assert.False(AddsNullOrdering(e => e.UserData!.Sum(u => u.PlayCount)));
        Assert.False(AddsNullOrdering(e => e.UserData!.Any(u => u.Played)));
        Assert.False(AddsNullOrdering(e => e.UserData!.Select(u => (bool?)u.IsFavorite).FirstOrDefault() ?? false));
        Assert.False(AddsNullOrdering(OrderMapper.MapSearchRelevanceOrder("term")));

        // Entity Framework Core compares NULL as C# does, so a comparison is never NULL itself.
        Assert.False(AddsNullOrdering(e => e.UserData!.FirstOrDefault()!.PlayCount > 0));
    }

    [Theory]
    [MemberData(nameof(SortKeysThatCanBeNull))]
    public void ApplyOrder_SortKeyThatCanBeNull_AddsNullOrdering(ItemSortBy sortBy)
    {
        Assert.True(AddsNullOrdering(sortBy));
    }

    [Theory]
    [MemberData(nameof(SortKeysThatCannotBeNull))]
    public void ApplyOrder_SortKeyThatCannotBeNull_IsLeftAlone(ItemSortBy sortBy)
    {
        Assert.False(AddsNullOrdering(sortBy));
    }

    public void Dispose() => _context.Dispose();

    // The orderings of a query, innermost first.
    private static List<(string Method, LambdaExpression Key)> Orderings(Expression expression)
    {
        var orderings = new List<(string Method, LambdaExpression Key)>();
        while (expression is MethodCallExpression call)
        {
            if (call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending))
            {
                orderings.Add((call.Method.Name, (LambdaExpression)((UnaryExpression)call.Arguments[1]).Operand));
            }

            expression = call.Arguments[0];
        }

        orderings.Reverse();
        return orderings;
    }

    private Expression Rewrite<T>(IQueryable<T> query)
        => NullsSortLowInterceptor.Rewrite(query.Expression, _context.Model);

    private bool AddsNullOrdering<TKey>(Expression<Func<BaseItemEntity, TKey>> key)
        => Orderings(Rewrite(_context.BaseItems.OrderBy(key))).Count == 2;

    // Whether an ordering goes in front of the first one the repository orders items by.
    private bool AddsNullOrdering(ItemSortBy sortBy)
    {
        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        var repository = new BaseItemRepository(
            Mock.Of<IDbContextFactory<JellyfinDbContext>>(),
            Mock.Of<IServerApplicationHost>(),
            new ItemTypeLookup(),
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);
        var filter = new InternalItemsQuery(new User("test", "auth-provider", "reset-provider")) { OrderBy = [(sortBy, SortOrder.Ascending)] };

        var query = repository.ApplyOrder(_context.BaseItems, filter, _context);

        return !ReferenceEquals(Orderings(query.Expression)[0].Key, Orderings(Rewrite(query))[0].Key);
    }
}

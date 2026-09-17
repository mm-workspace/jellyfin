using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Activity;
using Jellyfin.Server.Implementations.Item;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;
using ItemSortBy = Jellyfin.Data.Enums.ItemSortBy;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Paging through rows whose sort keys are equal must return every row exactly once.
/// </summary>
public sealed class PagingTiebreakTests : DbTestFixture
{
    private const int RowCount = 50;
    private const int PageSize = 7;
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    private static readonly DateTime _sameDate = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ItemSortBy.SortName)]
    [InlineData(ItemSortBy.ProductionYear)]
    [InlineData(ItemSortBy.DateCreated)]
    public void GetItems_EqualSortKeys_ReturnsEveryItemOnce(ItemSortBy sortBy)
    {
        var ids = new List<Guid>();
        using (var context = CreateDbContext())
        {
            for (var i = 0; i < RowCount; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                context.BaseItems.Add(new BaseItemEntity { Id = id, Type = MovieType, Name = "Same", SortName = "same", ProductionYear = 2000, DateCreated = _sameDate, PresentationUniqueKey = id.ToString("N") });
            }

            context.SaveChanges();
        }

        var repository = CreateBaseItemRepository(new ItemTypeLookup());
        var paged = Pages(start => repository.GetItems(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            OrderBy = [(sortBy, SortOrder.Ascending)],
            StartIndex = start,
            Limit = PageSize
        }).Items.Select(i => i.Id));

        Assert.Equal(ids.Order(), paged.Order());
    }

    [Fact]
    public void GetPeople_EqualSortKeys_ReturnsEveryPersonOnce()
    {
        var itemId = Guid.NewGuid();
        var peopleIds = new List<Guid>();
        using (var context = CreateDbContext())
        {
            var item = new BaseItemEntity { Id = itemId, Type = MovieType, Name = "Movie", PresentationUniqueKey = itemId.ToString("N") };
            context.BaseItems.Add(item);
            for (var i = 0; i < RowCount; i++)
            {
                var person = new People { Id = Guid.NewGuid(), Name = "Same", PersonType = "Actor" };
                peopleIds.Add(person.Id);
                context.Peoples.Add(person);
                context.PeopleBaseItemMap.Add(new PeopleBaseItemMap { ItemId = itemId, Item = item, PeopleId = person.Id, People = person, ListOrder = 0, Role = "Role " + i });
            }

            context.SaveChanges();
        }

        var repository = new PeopleRepository(CreateDbContextFactory(), new ItemTypeLookup(), Mock.Of<IItemQueryHelpers>());
        var paged = Pages(start => repository.GetPeople(new InternalPeopleQuery { ItemId = itemId, StartIndex = start, Limit = PageSize }).Items.Select(p => p.Id));

        Assert.Equal(peopleIds.Order(), paged.Order());
    }

    [Fact]
    public async Task GetPagedResultAsync_EqualDates_ReturnsEveryEntryOnce()
    {
        await using (var context = CreateDbContext())
        {
            for (var i = 0; i < RowCount; i++)
            {
                context.ActivityLogs.Add(new ActivityLog("Same", "Type", Guid.Empty) { DateCreated = _sameDate });
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var manager = new ActivityManager(CreateDbContextFactory());
        var paged = new List<long>();
        for (var start = 0; start < RowCount; start += PageSize)
        {
            var page = await manager.GetPagedResultAsync(new ActivityLogQuery { Skip = start, Limit = PageSize });
            paged.AddRange(page.Items.Select(e => e.Id));
        }

        Assert.Equal(RowCount, paged.Distinct().Count());
        Assert.Equal(RowCount, paged.Count);
    }

    [Fact]
    public async Task GetTrickplayItemsAsync_SameItem_ReturnsEveryResolutionOnce()
    {
        var itemId = Guid.NewGuid();
        await using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = itemId, Type = MovieType, Name = "Movie", PresentationUniqueKey = itemId.ToString("N") });
            for (var i = 0; i < RowCount; i++)
            {
                context.TrickplayInfos.Add(new TrickplayInfo { ItemId = itemId, Width = 100 + i });
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var manager = new TrickplayManager(NullLogger<TrickplayManager>.Instance, null!, null!, null!, null!, null!, CreateDbContextFactory(), ApplicationPaths, null!);
        var paged = new List<int>();
        for (var start = 0; start < RowCount; start += PageSize)
        {
            paged.AddRange((await manager.GetTrickplayItemsAsync(PageSize, start)).Select(t => t.Width));
        }

        Assert.Equal(Enumerable.Range(100, RowCount), paged.Order());
    }

    private static List<Guid> Pages(Func<int, IEnumerable<Guid>> getPage)
    {
        var ids = new List<Guid>();
        for (var start = 0; start < RowCount; start += PageSize)
        {
            ids.AddRange(getPage(start));
        }

        return ids;
    }
}

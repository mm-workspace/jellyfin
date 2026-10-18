using System;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;
using ItemSortBy = Jellyfin.Data.Enums.ItemSortBy;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Ordering by <see cref="ItemSortBy.PremiereDate"/> places an item that only has a production year at the
/// first day of that year, among the items with a full premiere date.
/// </summary>
public sealed class BaseItemRepositoryPremiereDateOrderingTests : SqliteDbTestFixture
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    // Chronological order. The names run against it, so year-only items that sorted alike would come
    // back in name order instead.
    private static readonly string[] _chronological =
    [
        "H year 1895",
        "G dated 1899-12-31",
        "F year 1900",
        "E year 1950",
        "D dated 1950-06-15",
        "C dated 1960-05-05, year 2040",
        "B dated 2029-12-31",
        "A year 2030"
    ];

    private readonly BaseItemRepository _repository;

    public BaseItemRepositoryPremiereDateOrderingTests()
    {
        using (var context = CreateDbContext())
        {
            AddMovie(context, "A year 2030", productionYear: 2030);
            AddMovie(context, "B dated 2029-12-31", premiereDate: new DateTime(2029, 12, 31, 0, 0, 0, DateTimeKind.Utc));
            AddMovie(context, "C dated 1960-05-05, year 2040", premiereDate: new DateTime(1960, 5, 5, 0, 0, 0, DateTimeKind.Utc), productionYear: 2040);
            AddMovie(context, "D dated 1950-06-15", premiereDate: new DateTime(1950, 6, 15, 0, 0, 0, DateTimeKind.Utc));
            AddMovie(context, "E year 1950", productionYear: 1950);
            AddMovie(context, "F year 1900", productionYear: 1900);
            AddMovie(context, "G dated 1899-12-31", premiereDate: new DateTime(1899, 12, 31, 0, 0, 0, DateTimeKind.Utc));
            AddMovie(context, "H year 1895", productionYear: 1895);
            context.SaveChanges();
        }

        _repository = CreateBaseItemRepository(new ItemTypeLookup());
    }

    [Theory]
    [InlineData(SortOrder.Ascending)]
    [InlineData(SortOrder.Descending)]
    public void GetItemList_PremiereDate_PlacesYearOnlyItemsAtTheStartOfTheirYear(SortOrder sortOrder)
    {
        var names = _repository
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                OrderBy = [(ItemSortBy.PremiereDate, sortOrder)]
            })
            .Select(i => i.Name);

        Assert.Equal(sortOrder == SortOrder.Ascending ? _chronological : _chronological.Reverse(), names);
    }

    private static void AddMovie(JellyfinDbContext context, string name, DateTime? premiereDate = null, int? productionYear = null)
    {
        var id = Guid.NewGuid();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = MovieType,
            Name = name,
            SortName = name,
            PresentationUniqueKey = id.ToString("N"),
            PremiereDate = premiereDate,
            ProductionYear = productionYear
        });
    }
}

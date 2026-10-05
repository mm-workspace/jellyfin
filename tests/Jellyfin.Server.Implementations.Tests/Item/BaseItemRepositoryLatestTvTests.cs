using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// The "Latest" row of a TV library looks at the episodes added within a day of each series' newest one.
/// That day cannot be measured back from an episode with no creation date, or from one dated less than a
/// day after <see cref="DateTime.MinValue"/>.
/// </summary>
public sealed class BaseItemRepositoryLatestTvTests : SqliteDbTestFixture
{
    private readonly ItemTypeLookup _itemTypeLookup = new();

    [Fact]
    public void GetLatestItemList_NoEpisodeHasADateCreated_ReturnsTheEpisode()
    {
        using (var context = CreateDbContext())
        {
            AddSeries(context, "Undated", 1, null);
            context.SaveChanges();
        }

        Assert.Equal(["Undated Episode"], GetLatestTvShows().Select(i => i.Name));
    }

    [Fact]
    public void GetLatestItemList_EpisodeDatedOnTheFirstDayOfTheCalendar_ReturnsIt()
    {
        using (var context = CreateDbContext())
        {
            AddSeries(context, "Ancient", 1, new DateTime(1, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            context.SaveChanges();
        }

        Assert.Equal(["Ancient Episode"], GetLatestTvShows().Select(i => i.Name));
    }

    private void AddSeries(JellyfinDbContext context, string seriesName, int ordinal, DateTime? dateCreated)
    {
        var seriesId = new Guid($"{ordinal:D8}-0000-0000-0000-000000000000");
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = seriesId,
            Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Series],
            Name = seriesName,
            PresentationUniqueKey = seriesId.ToString("N"),
            IsFolder = true,
            DateCreated = dateCreated
        });

        var episodeId = new Guid($"{ordinal:D8}-0000-0000-0000-000000000001");
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = episodeId,
            Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode],
            Name = $"{seriesName} Episode",
            SeriesName = seriesName,
            SeriesId = seriesId,
            PresentationUniqueKey = episodeId.ToString("N"),
            MediaType = "Video",
            DateCreated = dateCreated
        });
    }

    // The query a TV library's "Latest" row is read with: leaf items only, newest first.
    private IReadOnlyList<BaseItem> GetLatestTvShows()
        => CreateBaseItemRepository(_itemTypeLookup)
            .GetLatestItemList(new InternalItemsQuery { IsFolder = false, Limit = 16 }, CollectionType.tvshows);
}

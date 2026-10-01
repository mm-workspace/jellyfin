using System;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// The listing a music library's cover image is drawn from, as it reaches the database: CollectionFolderImageProvider
/// asks the library manager for eight random artists with a primary image below the library, and the library manager
/// hands the repository the library's physical folders as top parents.
/// </summary>
public sealed class BaseItemRepositoryLibraryCoverArtistsTests : DbTestFixture
{
    private static readonly Guid _library = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid _otherLibrary = Guid.Parse("44444444-0000-0000-0000-000000000001");

    private readonly ItemTypeLookup _itemTypeLookup = new();
    private readonly BaseItemRepository _repository;

    public BaseItemRepositoryLibraryCoverArtistsTests()
    {
        _repository = CreateBaseItemRepository(_itemTypeLookup);
    }

    [Fact]
    public void GetAllArtists_CoverQuery_ReturnsTheLibrarysArtistsWithAPrimaryImage()
    {
        Seed(_library, ("Artist", ItemValueType.Artist, true), ("Album Artist", ItemValueType.AlbumArtist, true), ("No Image", ItemValueType.Artist, false));
        Seed(_otherLibrary, ("Elsewhere", ItemValueType.Artist, true));

        Assert.Equal(["Album Artist", "Artist"], Names(CoverArtists()));
    }

    [Fact]
    public void GetAllArtists_CoverQuery_TakesEightDistinctArtists()
    {
        var names = Enumerable.Range(0, 12).Select(i => $"Artist {i:D2}").ToArray();
        Seed(_library, names.Select(name => (name, ItemValueType.Artist, true)).ToArray());

        var result = Names(CoverArtists());

        Assert.Equal(8, result.Length);
        Assert.Equal(8, result.Distinct(StringComparer.Ordinal).Count());
        Assert.Subset(names.ToHashSet(StringComparer.Ordinal), result.ToHashSet(StringComparer.Ordinal));
    }

    private static string[] Names(QueryResult<(BaseItem Item, MediaBrowser.Model.Dto.ItemCounts? ItemCounts)> result)
        => result.Items.Select(i => i.Item.Name!).Order(StringComparer.Ordinal).ToArray();

    private QueryResult<(BaseItem Item, MediaBrowser.Model.Dto.ItemCounts? ItemCounts)> CoverArtists()
    {
        return _repository.GetAllArtists(new InternalItemsQuery
        {
            TopParentIds = [_library],
            DtoOptions = new DtoOptions(false),
            EnableTotalRecordCount = false,
            ImageTypes = [ImageType.Primary],
            Limit = 8,
            OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)]
        });
    }

    /// <summary>
    /// Seeds by-name artists and, in the given library, one track crediting each of them.
    /// </summary>
    /// <param name="topParentId">The library the tracks belong to.</param>
    /// <param name="artists">The artists: name, how the track credits them, and whether they have a primary image.</param>
    private void Seed(Guid topParentId, params (string Name, ItemValueType Credit, bool HasImage)[] artists)
    {
        using var context = CreateDbContext();
        foreach (var (name, credit, hasImage) in artists)
        {
            var artistId = Guid.NewGuid();
            var artist = new BaseItemEntity
            {
                Id = artistId,
                Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.MusicArtist],
                Name = name,
                CleanName = name.GetCleanValue(),
                PresentationUniqueKey = artistId.ToString("N"),
                IsFolder = true
            };

            if (hasImage)
            {
                artist.Images =
                [
                    new BaseItemImageInfo
                    {
                        Id = Guid.NewGuid(),
                        ItemId = artistId,
                        Item = artist,
                        ImageType = ImageInfoImageType.Primary,
                        Path = $"/metadata/artists/{name}/folder.jpg"
                    }
                ];
            }

            var trackId = Guid.NewGuid();
            var track = new BaseItemEntity
            {
                Id = trackId,
                Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Audio],
                Name = $"{name} - Track",
                CleanName = $"{name} - Track".GetCleanValue(),
                PresentationUniqueKey = trackId.ToString("N"),
                MediaType = "Audio",
                TopParentId = topParentId
            };

            var itemValue = new ItemValue
            {
                ItemValueId = Guid.NewGuid(),
                Type = credit,
                Value = name,
                CleanValue = name.GetCleanValue()
            };

            context.BaseItems.AddRange(artist, track);
            context.ItemValues.Add(itemValue);
            context.ItemValuesMap.Add(new ItemValueMap { ItemId = trackId, ItemValueId = itemValue.ItemValueId, Item = track, ItemValue = itemValue });
        }

        context.SaveChanges();
    }
}

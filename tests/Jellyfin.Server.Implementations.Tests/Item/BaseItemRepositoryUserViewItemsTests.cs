using System;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// The query a playlist or collection library's view is shown or hidden by, as it reaches the database: the user view
/// manager asks for items of the library's kind below the library's physical folders, for the user the views are for.
/// </summary>
public sealed class BaseItemRepositoryUserViewItemsTests : DbTestFixture
{
    private static readonly Guid _library = Guid.Parse("55555555-0000-0000-0000-000000000001");
    private static readonly Guid _secondFolder = Guid.Parse("55555555-0000-0000-0000-000000000002");
    private static readonly Guid _otherLibrary = Guid.Parse("66666666-0000-0000-0000-000000000001");

    private readonly ItemTypeLookup _itemTypeLookup = new();
    private readonly BaseItemRepository _repository;
    private readonly User _user = new("user", "auth-provider", "reset-provider");

    public BaseItemRepositoryUserViewItemsTests()
    {
        _repository = CreateBaseItemRepository(_itemTypeLookup);
    }

    [Fact]
    public void GetItemList_PlaylistViewQuery_ReturnsThePlaylistsOfTheLibrary()
    {
        Seed(_library, ("Road Trip", BaseItemKind.Playlist), ("Workout", BaseItemKind.Playlist), ("Trilogy", BaseItemKind.BoxSet));
        Seed(_otherLibrary, ("Elsewhere", BaseItemKind.Playlist));

        Assert.Equal(["Road Trip", "Workout"], ViewItems(BaseItemKind.Playlist, _library));
    }

    [Fact]
    public void GetItemList_PlaylistViewQuery_CoversEveryPhysicalFolderOfTheLibrary()
    {
        Seed(_library, ("Road Trip", BaseItemKind.Playlist));
        Seed(_secondFolder, ("Workout", BaseItemKind.Playlist));

        Assert.Equal(["Road Trip", "Workout"], ViewItems(BaseItemKind.Playlist, _library, _secondFolder));
    }

    [Fact]
    public void GetItemList_PlaylistViewQuery_LibraryWithoutPlaylists_ReturnsNothing()
    {
        Seed(_library, ("Trilogy", BaseItemKind.BoxSet));
        Seed(_otherLibrary, ("Elsewhere", BaseItemKind.Playlist));

        Assert.Empty(ViewItems(BaseItemKind.Playlist, _library));
    }

    private string[] ViewItems(BaseItemKind kind, params Guid[] topParentIds)
    {
        return _repository.GetItemList(new InternalItemsQuery(_user)
        {
            IncludeItemTypes = [kind],
            TopParentIds = topParentIds,
            GroupByPresentationUniqueKey = false,
            DtoOptions = DtoOptions.StoredColumnsOnly
        })
            .Select(item => item.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private void Seed(Guid topParentId, params (string Name, BaseItemKind Kind)[] items)
    {
        using var context = CreateDbContext();
        foreach (var (name, kind) in items)
        {
            var id = Guid.NewGuid();
            context.BaseItems.Add(new BaseItemEntity
            {
                Id = id,
                Type = _itemTypeLookup.BaseItemKindNames[kind],
                Name = name,
                PresentationUniqueKey = id.ToString("N"),
                IsFolder = true,
                TopParentId = topParentId
            });
        }

        context.SaveChanges();
    }
}

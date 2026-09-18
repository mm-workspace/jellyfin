using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;
using ItemSortBy = Jellyfin.Data.Enums.ItemSortBy;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// The played and resumable filters treat folders by their descendants. A query that cannot return a folder
/// must not carry that part: the database plans and costs it for every row even though no row reaches it.
/// </summary>
public sealed class BaseItemRepositoryFolderFilterTests : SqliteDbTestFixture
{
    private const string SeriesType = "MediaBrowser.Controller.Entities.TV.Series";
    private const string EpisodeType = "MediaBrowser.Controller.Entities.TV.Episode";
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    // The descendants of a folder are only reachable through this table.
    private const string FolderPartMarker = "AncestorIds";

    private readonly ItemTypeLookup _itemTypeLookup = new();
    private readonly BaseItemRepository _repository;
    private readonly User _user = new("test", "auth-provider", "reset-provider");
    private readonly Guid _playedMovie = Guid.NewGuid();
    private readonly Guid _unplayedMovie = Guid.NewGuid();
    private readonly Guid _watchedSeries = Guid.NewGuid();
    private readonly Guid _unwatchedSeries = Guid.NewGuid();

    public BaseItemRepositoryFolderFilterTests()
    {
        using (var context = CreateDbContext())
        {
            Seed(context);
        }

        _repository = CreateBaseItemRepository(_itemTypeLookup);
    }

    public static TheoryData<BaseItemKind> MappedKinds()
        => new(new ItemTypeLookup().BaseItemKindNames.Keys);

    [Theory]
    [MemberData(nameof(MappedKinds))]
    public void IsPlayed_OneKind_CarriesTheFolderPartOnlyForFolderKinds(BaseItemKind kind)
    {
        var type = ResolveType(_itemTypeLookup.BaseItemKindNames[kind]);

        var sql = Sql(new InternalItemsQuery(_user) { IncludeItemTypes = [kind], IsPlayed = false });

        // Only a kind whose class is known not to be a folder may lose the folder part.
        var canBeFolder = typeof(Folder).IsAssignableFrom(type) || !typeof(BaseItem).IsAssignableFrom(type);
        Assert.Equal(canBeFolder, sql.Contains(FolderPartMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void IsPlayed_LeafAndFolderKinds_KeepsTheFolderPart()
    {
        Assert.Contains(FolderPartMarker, Sql(new InternalItemsQuery(_user) { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series], IsPlayed = true }), StringComparison.Ordinal);
    }

    [Fact]
    public void IsPlayed_NoKinds_KeepsTheFolderPart()
    {
        Assert.Contains(FolderPartMarker, Sql(new InternalItemsQuery(_user) { IsPlayed = true }), StringComparison.Ordinal);
    }

    [Fact]
    public void IsPlayed_NoKindsButNoFolders_LeavesTheFolderPartOut()
    {
        Assert.DoesNotContain(FolderPartMarker, Sql(new InternalItemsQuery(_user) { IsFolder = false, IsPlayed = true }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsResumable_LeafKind_LeavesTheFolderPartOut(bool isResumable)
    {
        Assert.DoesNotContain(FolderPartMarker, Sql(new InternalItemsQuery(_user) { IncludeItemTypes = [BaseItemKind.Movie], IsResumable = isResumable }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsResumable_FolderKind_KeepsTheFolderPart(bool isResumable)
    {
        Assert.Contains(FolderPartMarker, Sql(new InternalItemsQuery(_user) { IncludeItemTypes = [BaseItemKind.Series], IsResumable = isResumable }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsPlayed_Movies_ReturnsMoviesByTheirOwnState(bool isPlayed)
    {
        var ids = Ids(new InternalItemsQuery(_user) { IncludeItemTypes = [BaseItemKind.Movie], IsPlayed = isPlayed });

        Assert.Equal([isPlayed ? _playedMovie : _unplayedMovie], ids);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsPlayed_Series_ReturnsSeriesByTheirEpisodes(bool isPlayed)
    {
        var ids = Ids(new InternalItemsQuery(_user) { IncludeItemTypes = [BaseItemKind.Series], IsPlayed = isPlayed });

        Assert.Equal([isPlayed ? _watchedSeries : _unwatchedSeries], ids);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsPlayed_NoKindsButNoFolders_ReturnsLeafItemsOnly(bool isPlayed)
    {
        var withoutFolders = Ids(new InternalItemsQuery(_user) { IsFolder = false, IsPlayed = isPlayed });
        var everything = Ids(new InternalItemsQuery(_user) { IsPlayed = isPlayed });

        Assert.DoesNotContain(isPlayed ? _watchedSeries : _unwatchedSeries, withoutFolders);
        Assert.Contains(isPlayed ? _watchedSeries : _unwatchedSeries, everything);
        Assert.Equal(everything.Where(id => !id.Equals(_watchedSeries) && !id.Equals(_unwatchedSeries)).Order(), withoutFolders.Order());
    }

    [Fact]
    public void IsPlayedOrder_Movies_PlacesTheUnplayedMovieFirst()
    {
        var query = new InternalItemsQuery(_user) { IncludeItemTypes = [BaseItemKind.Movie], OrderBy = [(ItemSortBy.IsPlayed, SortOrder.Ascending)] };

        Assert.Equal([_unplayedMovie, _playedMovie], _repository.GetItemList(query).Select(i => i.Id));
    }

    private static Type ResolveType(string fullName)
    {
        // Touch the assemblies that hold item classes so that they are loaded.
        _ = typeof(Folder);
        _ = typeof(ItemTypeLookup);
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName)).FirstOrDefault(t => t is not null);
        Assert.NotNull(type);
        return type;
    }

    private string Sql(InternalItemsQuery query)
    {
        using var context = CreateDbContext();
        return _repository.TranslateQuery(context.BaseItems.AsNoTracking(), context, query).ToQueryString();
    }

    private List<Guid> Ids(InternalItemsQuery query)
        => _repository.GetItemList(query).Select(i => i.Id).ToList();

    private void Seed(JellyfinDbContext context)
    {
        context.Users.Add(_user);

        AddItem(context, _playedMovie, MovieType, "A played movie", isFolder: false);
        AddItem(context, _unplayedMovie, MovieType, "B unplayed movie", isFolder: false);
        AddPlayedUserData(context, _playedMovie);

        AddSeries(context, _watchedSeries, "C watched series", played: true);
        AddSeries(context, _unwatchedSeries, "D unwatched series", played: false);

        context.SaveChanges();
    }

    private void AddSeries(JellyfinDbContext context, Guid id, string name, bool played)
    {
        AddItem(context, id, SeriesType, name, isFolder: true);
        var episodeId = Guid.NewGuid();
        AddItem(context, episodeId, EpisodeType, name + " episode", isFolder: false);
        context.AncestorIds.Add(new AncestorId { ItemId = episodeId, ParentItemId = id, Item = null!, ParentItem = null! });
        if (played)
        {
            AddPlayedUserData(context, episodeId);
        }
    }

    private static void AddItem(JellyfinDbContext context, Guid id, string type, string name, bool isFolder)
        => context.BaseItems.Add(new BaseItemEntity { Id = id, Type = type, Name = name, SortName = name, PresentationUniqueKey = id.ToString("N"), IsFolder = isFolder });

    private void AddPlayedUserData(JellyfinDbContext context, Guid itemId)
        => context.UserData.Add(new UserData
        {
            ItemId = itemId,
            UserId = _user.Id,
            CustomDataKey = itemId.ToString("N"),
            Played = true,
            Item = null!,
            User = null!
        });
}

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;
using ItemSortBy = Jellyfin.Data.Enums.ItemSortBy;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// A series asks for its seasons and episodes by its own presentation key, and the items are grouped by theirs, so
/// that a series kept in two libraries lists each season and each episode once. PostgreSQL computes the group
/// representatives of such a query up front, in a common table expression.
/// </summary>
public sealed class BaseItemRepositorySeriesGroupingTests : DbTestFixture
{
    private const string SeriesKey = "series";
    private const string OtherSeriesKey = "other-series";

    private static readonly Guid _tvLibraryId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _tv4KLibraryId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly CommandRecorder _recorder;
    private readonly ItemTypeLookup _itemTypeLookup = new();
    private readonly BaseItemRepository _repository;
    private readonly User _user = new("test", "auth-provider", "reset-provider");

    private readonly Guid _season1Id = Guid.Parse("c1000000-0000-0000-0000-000000000000");
    private readonly Guid _season2Id = Guid.Parse("c2000000-0000-0000-0000-000000000000");
    private readonly Guid _episode1Id = Guid.Parse("e1010000-0000-0000-0000-000000000000");
    private readonly Guid _episode2Id = Guid.Parse("e1020000-0000-0000-0000-000000000000");
    private readonly Guid _episode3Id = Guid.Parse("e2010000-0000-0000-0000-000000000000");

    public BaseItemRepositorySeriesGroupingTests()
        : this(new CommandRecorder())
    {
    }

    private BaseItemRepositorySeriesGroupingTests(CommandRecorder recorder)
        : base(recorder)
    {
        _recorder = recorder;
        using (var context = CreateDbContext())
        {
            Seed(context);
        }

        _repository = CreateBaseItemRepository(_itemTypeLookup);
    }

    [Fact]
    public void GetItemList_SeasonsOfASeries_ReturnsEachSeasonOnce()
    {
        var ids = Ids(BaseItemKind.Season);

        // The first season of the 4K library shares its key with the one of the other library, which has the lower id.
        Assert.Equal([_season1Id, _season2Id], ids);
        AssertRepresentativesComputedUpFront();
    }

    [Fact]
    public void GetItemList_EpisodesOfASeries_ReturnsThePrimaryOfEachEpisode()
    {
        var ids = Ids(BaseItemKind.Episode);

        // The 4K version sorts first by id and is not hidden behind a primary of its own library, so only the
        // grouping folds it into its primary.
        Assert.Equal([_episode1Id, _episode2Id, _episode3Id], ids);
        AssertRepresentativesComputedUpFront();
    }

    [Fact]
    public void GetItemList_SeasonsAndEpisodesOfASeries_ReturnsEachOnce()
    {
        var ids = Ids(BaseItemKind.Episode, BaseItemKind.Season);

        Assert.Equal([_episode1Id, _episode2Id, _episode3Id, _season1Id, _season2Id], ids);
        AssertRepresentativesComputedUpFront();
    }

    private List<Guid> Ids(params BaseItemKind[] kinds)
    {
        _recorder.Commands.Clear();

        // The query a series builds for its seasons and episodes, for a user who does not display missing episodes.
        var query = new InternalItemsQuery(_user)
        {
            AncestorWithPresentationUniqueKey = null,
            SeriesPresentationUniqueKey = SeriesKey,
            IncludeItemTypes = kinds,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            IsMissing = false
        };

        return _repository.GetItemList(query).Select(i => i.Id).ToList();
    }

    private void AssertRepresentativesComputedUpFront()
    {
        // Only PostgreSQL rewrites the statement; the assertion proves that these queries reach that rewrite.
        if (Database is PostgreSqlTestDatabase)
        {
            var sql = Assert.Single(_recorder.Commands);
            Assert.StartsWith("WITH \"__GroupRepresentatives1\" AS MATERIALIZED (", sql, StringComparison.Ordinal);
        }
    }

    private void Seed(JellyfinDbContext context)
    {
        context.Users.Add(_user);
        AddLibrary(context, _tvLibraryId, "TV");
        AddLibrary(context, _tv4KLibraryId, "TV-4K");

        var seriesId = Guid.NewGuid();
        Add(context, BaseItemKind.Series, seriesId, _tvLibraryId, _tvLibraryId, "Series", SeriesKey, seriesKey: null);
        Add(context, BaseItemKind.Season, _season1Id, seriesId, _tvLibraryId, "Season 1", SeriesKey + "-001");
        Add(context, BaseItemKind.Season, _season2Id, seriesId, _tvLibraryId, "Season 2", SeriesKey + "-002");
        Add(context, BaseItemKind.Episode, _episode1Id, _season1Id, _tvLibraryId, "001-001", SeriesKey + "-001001");
        Add(context, BaseItemKind.Episode, _episode2Id, _season1Id, _tvLibraryId, "001-002", SeriesKey + "-001002");
        Add(context, BaseItemKind.Episode, _episode3Id, _season2Id, _tvLibraryId, "002-001", SeriesKey + "-002001");
        Add(context, BaseItemKind.Episode, Guid.NewGuid(), _season2Id, _tvLibraryId, "002-002", SeriesKey + "-002002").IsVirtualItem = true;

        // The same series in the 4K library, holding the 4K version of the first episode.
        var series4KId = Guid.NewGuid();
        var season4KId = Guid.Parse("f1000000-0000-0000-0000-000000000000");
        Add(context, BaseItemKind.Series, series4KId, _tv4KLibraryId, _tv4KLibraryId, "Series", SeriesKey, seriesKey: null);
        Add(context, BaseItemKind.Season, season4KId, series4KId, _tv4KLibraryId, "Season 1", SeriesKey + "-001");
        Add(context, BaseItemKind.Episode, Guid.Parse("0e101000-0000-0000-0000-000000000000"), season4KId, _tv4KLibraryId, "001-001", SeriesKey + "-001001")
            .PrimaryVersionId = _episode1Id;

        // Another series, which none of the queries may return.
        var otherSeriesId = Guid.NewGuid();
        var otherSeasonId = Guid.NewGuid();
        Add(context, BaseItemKind.Series, otherSeriesId, _tvLibraryId, _tvLibraryId, "Other series", OtherSeriesKey, seriesKey: null);
        Add(context, BaseItemKind.Season, otherSeasonId, otherSeriesId, _tvLibraryId, "Season 1", OtherSeriesKey + "-001", OtherSeriesKey);
        Add(context, BaseItemKind.Episode, Guid.NewGuid(), otherSeasonId, _tvLibraryId, "001-001", OtherSeriesKey + "-001001", OtherSeriesKey);

        context.SaveChanges();
    }

    private void AddLibrary(JellyfinDbContext context, Guid id, string name)
        => context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Folder],
            Name = name,
            Path = "/" + name,
            IsFolder = true
        });

    private BaseItemEntity Add(
        JellyfinDbContext context,
        BaseItemKind kind,
        Guid id,
        Guid parentId,
        Guid libraryId,
        string name,
        string presentationKey,
        string? seriesKey = SeriesKey)
    {
        var item = new BaseItemEntity
        {
            Id = id,
            Type = _itemTypeLookup.BaseItemKindNames[kind],
            Name = name,
            SortName = name,
            PresentationUniqueKey = presentationKey,
            SeriesPresentationUniqueKey = seriesKey,
            ParentId = parentId,
            TopParentId = libraryId,
            IsFolder = kind != BaseItemKind.Episode
        };
        context.BaseItems.Add(item);
        return item;
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }
    }
}

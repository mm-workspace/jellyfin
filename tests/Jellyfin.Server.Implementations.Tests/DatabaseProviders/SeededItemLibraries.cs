using System;
using System.Collections.Generic;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

/// <summary>
/// One seeded library on SQLite and the same one on PostgreSQL, shared by a whole test class.
/// </summary>
/// <remarks>
/// A value is left out of every column an ordering reads. Sort names and clean names stay unique, NULL included,
/// because those are the two keys an item ordering can end in, so every ordering puts the rows in one order only.
/// </remarks>
public sealed class SeededItemLibraries : IDisposable
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";
    private const string SeriesType = "MediaBrowser.Controller.Entities.TV.Series";
    private const string EpisodeType = "MediaBrowser.Controller.Entities.TV.Episode";
    private const string FolderType = "MediaBrowser.Controller.Entities.Folder";

    // One movie for each column an ordering reads, plus one that has neither a premiere date nor a production year.
    private const int MovieCount = 19;

    private static readonly Guid _userId = Id(1);

    private readonly ITestDatabase? _sqlite;
    private readonly ITestDatabase? _postgreSql;
    private readonly BaseItemRepository? _sqliteRepository;
    private readonly BaseItemRepository? _postgreSqlRepository;

    public SeededItemLibraries()
    {
        if (TestDatabase.PostgreSqlConnectionString is not { } connectionString)
        {
            return;
        }

        var options = new TestDatabaseOptions { ApplicationPaths = new Mock<IApplicationPaths>().Object };
        _sqlite = new SqliteInMemoryTestDatabase(options);
        _postgreSql = new PostgreSqlTestDatabase(connectionString, options);
        Seed(_sqlite);
        Seed(_postgreSql);
        _sqliteRepository = Repository(_sqlite);
        _postgreSqlRepository = Repository(_postgreSql);
    }

    public IReadOnlyList<Guid> SqliteIds(ItemSortBy sortBy, SortOrder sortOrder, int? startIndex, int? limit)
        => Ids(_sqliteRepository, sortBy, sortOrder, startIndex, limit);

    public IReadOnlyList<Guid> PostgreSqlIds(ItemSortBy sortBy, SortOrder sortOrder, int? startIndex, int? limit)
        => Ids(_postgreSqlRepository, sortBy, sortOrder, startIndex, limit);

    public void Dispose()
    {
        _sqlite?.Dispose();
        _postgreSql?.Dispose();
    }

    private static Guid Id(int index) => new(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private static IReadOnlyList<Guid> Ids(BaseItemRepository? repository, ItemSortBy sortBy, SortOrder sortOrder, int? startIndex, int? limit)
    {
        Assert.SkipWhen(repository is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
        return repository.GetItemIdsList(new InternalItemsQuery(new User("parity", "auth-provider", "reset-provider") { Id = _userId })
        {
            Recursive = true,
            OrderBy = [(sortBy, sortOrder)],
            StartIndex = startIndex,
            Limit = limit
        });
    }

    private static void Seed(ITestDatabase database)
    {
        var user = new User("parity", "auth-provider", "reset-provider") { Id = _userId };
        var library = new BaseItemEntity
        {
            Id = Id(2),
            Type = FolderType,
            Name = "library",
            SortName = "library",
            CleanName = "library",
            PresentationUniqueKey = "library",
            IsFolder = true,
            TopParentId = Id(2)
        };

        List<BaseItemEntity> items = [library];
        for (var i = 0; i < MovieCount; i++)
        {
            items.Add(Movie(i, library.Id));
        }

        // An alternate version of a movie, which the played date of its group is read from as well.
        var alternate = Movie(MovieCount, library.Id);
        alternate.PrimaryVersionId = items[1].Id;
        alternate.PresentationUniqueKey = items[1].PresentationUniqueKey;
        items.Add(alternate);

        for (var i = 0; i < 2; i++)
        {
            var series = new BaseItemEntity
            {
                Id = Id(0x40 + i),
                Type = SeriesType,
                Name = $"series {i}",
                SortName = $"series {i}",
                CleanName = $"series {i}",
                PresentationUniqueKey = $"series-{i}",
                SeriesPresentationUniqueKey = $"series-{i}",
                IsFolder = true,
                ParentId = library.Id,
                TopParentId = library.Id
            };
            items.Add(series);
            items.Add(new BaseItemEntity
            {
                Id = Id(0x50 + i),
                Type = EpisodeType,
                MediaType = "Video",
                Name = $"episode {i}",
                SortName = $"episode {i}",
                CleanName = $"episode {i}",
                PresentationUniqueKey = $"episode-{i}",
                SeriesPresentationUniqueKey = series.SeriesPresentationUniqueKey,
                ParentIndexNumber = 1,
                IndexNumber = i + 1,
                ParentId = series.Id,
                TopParentId = library.Id
            });
        }

        using var context = database.CreateDbContext();
        context.Users.Add(user);
        context.BaseItems.AddRange(items);

        // The first movies are played, the later ones were never touched, so every key read through user data is
        // missing for some of the rows.
        for (var i = 0; i < 8; i++)
        {
            context.UserData.Add(UserData(items[i + 1].Id, user.Id, i));
        }

        context.UserData.Add(UserData(alternate.Id, user.Id, 9));
        context.UserData.Add(UserData(Id(0x50), user.Id, 3));

        var values = new[]
        {
            new ItemValue { ItemValueId = Id(0x60), Type = ItemValueType.Artist, Value = "Artist B", CleanValue = "artist b" },
            new ItemValue { ItemValueId = Id(0x61), Type = ItemValueType.Artist, Value = "Artist A", CleanValue = "artist a" },
            new ItemValue { ItemValueId = Id(0x62), Type = ItemValueType.AlbumArtist, Value = "Album Artist", CleanValue = "album artist" },
            new ItemValue { ItemValueId = Id(0x63), Type = ItemValueType.Studios, Value = "Studio", CleanValue = "studio" }
        };
        context.ItemValues.AddRange(values);
        for (var i = 0; i < values.Length; i++)
        {
            var item = items[i + 1];
            context.ItemValuesMap.Add(new ItemValueMap { ItemId = item.Id, ItemValueId = values[i].ItemValueId, Item = item, ItemValue = values[i] });
        }

        context.SaveChanges();
    }

    private static UserData UserData(Guid itemId, Guid userId, int index) => new()
    {
        ItemId = itemId,
        UserId = userId,
        CustomDataKey = itemId.ToString("N"),
        PlayCount = index,
        Played = index % 2 == 0,
        IsFavorite = index % 3 == 0,
        PlaybackPositionTicks = TimeSpan.TicksPerMinute * index,

        // One played item has no played date, so the date is missing for a row that has user data as well.
        LastPlayedDate = index == 2 ? null : new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index),
        Item = null!,
        User = null!
    };

    private static BaseItemEntity Movie(int index, Guid libraryId)
    {
        var item = new BaseItemEntity
        {
            Id = Id(0x10 + index),
            Type = MovieType,
            MediaType = "Video",
            IsMovie = true,
            PresentationUniqueKey = $"movie-{index:00}",
            Name = $"movie {index:00}",
            SortName = $"sort {index:00}",
            CleanName = $"clean {index:00}",
            ProductionYear = 1980 + index,
            PremiereDate = new DateTime(1980 + index, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            CommunityRating = 1f + index,
            CriticRating = 10f + index,
            RunTimeTicks = TimeSpan.TicksPerMinute * (60 + index),
            DateCreated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index),
            DateLastMediaAdded = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index),
            StartDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index),
            Album = $"album {index:00}",
            SeriesName = $"series name {index:00}",
            IndexNumber = index + 1,
            ParentIndexNumber = index + 1,
            TotalBitrate = 1000 + index,
            InheritedParentalRatingValue = index + 1,
            ParentId = libraryId,
            TopParentId = libraryId
        };

        // Every column an ordering reads is missing on one movie. Two movies share a missing sort name and two
        // more share a missing clean name, so the keys an ordering can end in tie as they do in a real library:
        // what keeps the order the same on both providers is the id the ordering ends with.
        switch (index)
        {
            case 0: item.SortName = null; break;
            case 1: item.Name = null; item.SortName = null; break;
            case 2: item.CleanName = null; break;
            case 19: item.CleanName = null; break;
            case 3: item.ProductionYear = null; break;
            case 4: item.PremiereDate = null; break;
            case 5: item.CommunityRating = null; break;
            case 6: item.CriticRating = null; break;
            case 7: item.RunTimeTicks = null; break;
            case 8: item.DateCreated = null; break;
            case 9: item.DateLastMediaAdded = null; break;
            case 10: item.StartDate = null; break;
            case 11: item.Album = null; break;
            case 12: item.SeriesName = null; break;
            case 13: item.IndexNumber = null; break;
            case 14: item.ParentIndexNumber = null; break;
            case 15: item.TotalBitrate = null; break;
            case 16: item.InheritedParentalRatingValue = null; break;
            case 17: item.ParentId = null; item.TopParentId = null; break;
            case 18: item.PremiereDate = null; item.ProductionYear = null; break;
            default: break;
        }

        return item;
    }

    private static BaseItemRepository Repository(ITestDatabase database)
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        return new BaseItemRepository(
            database.CreateDbContextFactory(),
            Mock.Of<IServerApplicationHost>(),
            new ItemTypeLookup(),
            configuration.Object,
            NullLogger<BaseItemRepository>.Instance);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// An item keeps one user data row per user data key, so a key a deleted item left on the placeholder can
/// already be taken on the item claiming it. Reattaching has to end with every row of an (item, user) pair
/// saying the same thing, whatever each side said before.
/// </summary>
public sealed class ItemPersistenceReattachUserDataTests : DbTestFixture
{
    private static readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime _watched = new(2023, 8, 14, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _started = new(2021, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly ItemPersistenceService _service;
    private readonly ILibraryManager? _previousLibraryManager;
    private readonly IServerConfigurationManager? _previousConfigurationManager;
    private readonly IRecordingsManager? _previousRecordingsManager;

    public ItemPersistenceReattachUserDataTests()
    {
        // BaseItem resolves these through process-wide statics; restored in Dispose.
        _previousLibraryManager = BaseItem.LibraryManager;
        _previousConfigurationManager = BaseItem.ConfigurationManager;
        _previousRecordingsManager = Video.RecordingsManager;

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        BaseItem.LibraryManager = libraryManager.Object;

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        BaseItem.ConfigurationManager = configurationManager.Object;

        // Video.SourceType consults this before it can produce user data keys.
        Video.RecordingsManager = new Mock<IRecordingsManager>().Object;

        _service = new ItemPersistenceService(
            CreateDbContextFactory(),
            new Mock<IServerApplicationHost>().Object,
            Database.Provider,
            NullLogger<ItemPersistenceService>.Instance);
    }

    [Fact]
    public async Task ReattachUserData_ReplacementAlreadyHoldsTheDetachedKeys_KeepsTheMostRecentPlay()
    {
        // The same film, replaced by a better rip: two items carrying the same provider ids, so the same
        // user data keys, and a user who watched the old file and had started the new one.
        var replaced = CreateMovie(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd01"));
        var replacement = CreateMovie(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd02"));
        Seed(replaced, replacement);
        AddRows(replaced.Id, replaced.GetUserDataKeys(), _watched, playCount: 9, positionTicks: 0, played: true);
        AddRows(replacement.Id, replacement.GetUserDataKeys(), _started, playCount: 1, positionTicks: 490);

        // Removing the old file detaches its rows onto the placeholder, where the reattach finds them.
        _service.DeleteItem([replaced.Id]);

        await _service.ReattachUserDataAsync(replacement, TestContext.Current.CancellationToken);

        using var context = CreateDbContext();
        var rows = context.UserData.Where(e => e.ItemId.Equals(replacement.Id)).ToList();
        Assert.Equal(
            replacement.GetUserDataKeys().Order(StringComparer.Ordinal),
            rows.Select(e => e.CustomDataKey).Order(StringComparer.Ordinal));
        Assert.All(rows, row =>
        {
            Assert.True(row.Played);
            Assert.Equal(9, row.PlayCount);
            Assert.Equal(0, row.PlaybackPositionTicks);
            Assert.Null(row.RetentionDate);
        });

        // The key only the removed item had stays detached, for whatever claims it next.
        Assert.Equal(
            [replaced.Id.ToString()],
            context.UserData.Where(e => e.ItemId.Equals(BaseItemRepository.PlaceholderId)).Select(e => e.CustomDataKey).ToList());
    }

    [Fact]
    public async Task ReattachUserData_DetachedRowsFromDifferentEras_CollapsesToMostRecentPlay()
    {
        var movie = CreateMovie(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd03"));
        var keys = movie.GetUserDataKeys();
        Seed(movie);

        // The guid-keyed row was detached by an older deletion than the provider-keyed ones.
        AddRows(BaseItemRepository.PlaceholderId, [keys[^1]], _started, playCount: 7, positionTicks: 490, retentionDate: _watched);
        AddRows(BaseItemRepository.PlaceholderId, [keys[0], keys[1]], _watched, playCount: 9, positionTicks: 0, played: true, retentionDate: _watched);

        await _service.ReattachUserDataAsync(movie, TestContext.Current.CancellationToken);

        using var context = CreateDbContext();
        var rows = context.UserData.Where(e => e.ItemId.Equals(movie.Id)).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), rows.Select(e => e.CustomDataKey).Order(StringComparer.Ordinal));
        Assert.All(rows, row =>
        {
            Assert.True(row.Played);
            Assert.Equal(9, row.PlayCount);
            Assert.Equal(0, row.PlaybackPositionTicks);
            Assert.Null(row.RetentionDate);
        });

        Assert.Empty(context.UserData.Where(e => e.ItemId.Equals(BaseItemRepository.PlaceholderId)));
    }

    [Fact]
    public async Task ReattachUserData_NoDetachedRows_LeavesExistingRowsAlone()
    {
        var movie = CreateMovie(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd04"));
        Seed(movie);
        AddRows(movie.Id, [movie.GetUserDataKeys()[0]], _started, playCount: 1, positionTicks: 123);

        await _service.ReattachUserDataAsync(movie, TestContext.Current.CancellationToken);

        using var context = CreateDbContext();
        var row = Assert.Single(context.UserData.Where(e => e.ItemId.Equals(movie.Id)));
        Assert.Equal(123, row.PlaybackPositionTicks);
    }

    [Fact]
    public async Task ReattachUserData_NewestPlayCameFromTheDetachedRows_KeepsFavouriteAndRating()
    {
        var movie = CreateMovie(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd07"));
        var keys = movie.GetUserDataKeys();
        Seed(movie);

        // The user marked the item a favourite and rated it without ever playing it...
        AddRows(movie.Id, [keys[1], keys[2]], lastPlayed: null, playCount: 0, positionTicks: 0, isFavorite: true, rating: 8);

        // ...while the only play sits on a key an earlier incarnation of the item left detached. The two
        // sides share no key, so nothing forces them together beyond the reconcile itself.
        AddRows(BaseItemRepository.PlaceholderId, [keys[0]], _watched, playCount: 9, positionTicks: 0, played: true);

        await _service.ReattachUserDataAsync(movie, TestContext.Current.CancellationToken);

        using var context = CreateDbContext();
        var rows = context.UserData.Where(e => e.ItemId.Equals(movie.Id)).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), rows.Select(e => e.CustomDataKey).Order(StringComparer.Ordinal));
        Assert.All(rows, row =>
        {
            // The play state is the newest play's, but a favourite and a rating are not play state and
            // belong to the item the user is left with.
            Assert.True(row.Played);
            Assert.Equal(9, row.PlayCount);
            Assert.True(row.IsFavorite);
            Assert.Equal(8, row.Rating);
        });
    }

    [Fact]
    public async Task ReattachUserData_RowsOfSeveralUsers_ReconcilesEachUserOnItsOwn()
    {
        var movie = CreateMovie(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd05"));
        var keys = movie.GetUserDataKeys();
        Seed(movie);

        // One user's play detached with an earlier incarnation of the item; the other user's play is on the item.
        AddRows(BaseItemRepository.PlaceholderId, [keys[0]], _watched, playCount: 9, positionTicks: 0, played: true);
        AddRows(movie.Id, [keys[1]], _started, playCount: 2, positionTicks: 490, userId: _otherUserId);

        await _service.ReattachUserDataAsync(movie, TestContext.Current.CancellationToken);

        using var context = CreateDbContext();
        var rows = context.UserData.Where(e => e.ItemId.Equals(movie.Id)).ToList();
        Assert.All(rows.Where(e => e.UserId.Equals(_userId)), row => Assert.True(row.Played));
        Assert.All(rows.Where(e => e.UserId.Equals(_otherUserId)), row =>
        {
            Assert.False(row.Played);
            Assert.Equal(490, row.PlaybackPositionTicks);
        });

        // Each user now holds a row under every current key, and neither took the other's state.
        Assert.Equal(keys.Count, rows.Count(e => e.UserId.Equals(_userId)));
        Assert.Equal(keys.Count, rows.Count(e => e.UserId.Equals(_otherUserId)));
    }

    protected override void Dispose(bool disposing)
    {
        BaseItem.LibraryManager = _previousLibraryManager!;
        BaseItem.ConfigurationManager = _previousConfigurationManager!;
        Video.RecordingsManager = _previousRecordingsManager!;
        base.Dispose(disposing);
    }

    private static Movie CreateMovie(Guid id)
    {
        // GetUserDataKeys(): ["497698", "tt3480822", "<item id>"]
        return new Movie
        {
            Id = id,
            Name = "Black Widow",
            ProviderIds = new Dictionary<string, string>
            {
                ["Tmdb"] = "497698",
                ["Imdb"] = "tt3480822"
            }
        };
    }

    private void Seed(params BaseItem[] items)
    {
        using var context = CreateDbContext();
        context.Users.Add(new User("user", "auth-provider", "reset-provider") { Id = _userId });
        context.Users.Add(new User("other", "auth-provider", "reset-provider") { Id = _otherUserId });
        foreach (var item in items)
        {
            context.BaseItems.Add(new BaseItemEntity { Id = item.Id, Type = item.GetType().FullName! });
        }

        context.SaveChanges();
    }

    private void AddRows(
        Guid itemId,
        IReadOnlyList<string> keys,
        DateTime? lastPlayed,
        int playCount,
        long positionTicks,
        bool played = false,
        DateTime? retentionDate = null,
        Guid? userId = null,
        bool isFavorite = false,
        double? rating = null)
    {
        using var context = CreateDbContext();
        foreach (var key in keys)
        {
            context.UserData.Add(new UserData
            {
                ItemId = itemId,
                Item = null,
                UserId = userId ?? _userId,
                User = null,
                CustomDataKey = key,
                LastPlayedDate = lastPlayed,
                PlayCount = playCount,
                PlaybackPositionTicks = positionTicks,
                Played = played,
                RetentionDate = retentionDate,
                IsFavorite = isFavorite,
                Rating = rating
            });
        }

        context.SaveChanges();
    }
}

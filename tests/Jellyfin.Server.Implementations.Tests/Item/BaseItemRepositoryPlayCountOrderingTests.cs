using System;
using System.Collections.Generic;
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
/// Covers ordering by <see cref="ItemSortBy.PlayCount"/>, which reads the play count from the user's data row
/// of each item. An item the user never touched has no such row, so its play count is NULL and sorts below
/// every other one on every database, as it does on SQLite.
/// </summary>
public sealed class BaseItemRepositoryPlayCountOrderingTests : DbTestFixture
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    private readonly BaseItemRepository _repository;
    private readonly User _user = new("test", "auth-provider", "reset-provider");
    private readonly User _otherUser = new("other", "auth-provider", "reset-provider");

    // Sort names run A..G so that the tie break on sort name interleaves the three groups: a missing row
    // sorted among the play counts shows up as a different sequence rather than as the expected one by luck.
    private readonly Guid _neverTouched = Guid.NewGuid();
    private readonly Guid _playedThreeTimes = Guid.NewGuid();
    private readonly Guid _favouriteNeverPlayed = Guid.NewGuid();
    private readonly Guid _playedByTheOtherUserOnly = Guid.NewGuid();
    private readonly Guid _playedOnce = Guid.NewGuid();
    private readonly Guid _resumedNeverFinished = Guid.NewGuid();
    private readonly Guid _secondNeverTouched = Guid.NewGuid();

    public BaseItemRepositoryPlayCountOrderingTests()
    {
        using (var context = CreateDbContext())
        {
            Seed(context);
        }

        _repository = CreateBaseItemRepository(new ItemTypeLookup());
    }

    [Fact]
    public void PlayCountAscending_PutsItemsWithoutUserDataFirst()
    {
        Assert.Equal(
            [_neverTouched, _playedByTheOtherUserOnly, _secondNeverTouched, _favouriteNeverPlayed, _resumedNeverFinished, _playedOnce, _playedThreeTimes],
            MovieIds(SortOrder.Ascending));
    }

    [Fact]
    public void PlayCountDescending_PutsItemsWithoutUserDataLast()
    {
        Assert.Equal(
            [_playedThreeTimes, _playedOnce, _favouriteNeverPlayed, _resumedNeverFinished, _neverTouched, _playedByTheOtherUserOnly, _secondNeverTouched],
            MovieIds(SortOrder.Descending));
    }

    private List<Guid> MovieIds(SortOrder sortOrder)
        => _repository
            .GetItemList(new InternalItemsQuery(_user)
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                OrderBy = [(ItemSortBy.PlayCount, sortOrder)]
            })
            .Select(i => i.Id)
            .ToList();

    private void Seed(JellyfinDbContext context)
    {
        context.Users.AddRange(_user, _otherUser);

        AddMovie(context, _neverTouched, "A never touched");
        AddMovie(context, _playedThreeTimes, "B played three times");
        AddMovie(context, _favouriteNeverPlayed, "C favourite");
        AddMovie(context, _playedByTheOtherUserOnly, "D played by the other user");
        AddMovie(context, _playedOnce, "E played once");
        AddMovie(context, _resumedNeverFinished, "F resumed");
        AddMovie(context, _secondNeverTouched, "G never touched");

        AddUserData(context, _user, _playedThreeTimes, playCount: 3);
        AddUserData(context, _user, _favouriteNeverPlayed, isFavorite: true);
        AddUserData(context, _otherUser, _playedByTheOtherUserOnly, playCount: 5);
        AddUserData(context, _user, _playedOnce, playCount: 1);
        AddUserData(context, _user, _resumedNeverFinished, playbackPositionTicks: TimeSpan.TicksPerMinute);

        context.SaveChanges();
    }

    private static void AddMovie(JellyfinDbContext context, Guid id, string name)
        => context.BaseItems.Add(new BaseItemEntity { Id = id, Type = MovieType, Name = name, SortName = name, PresentationUniqueKey = id.ToString("N") });

    private static void AddUserData(JellyfinDbContext context, User user, Guid itemId, int playCount = 0, bool isFavorite = false, long playbackPositionTicks = 0)
        => context.UserData.Add(new UserData
        {
            ItemId = itemId,
            UserId = user.Id,
            CustomDataKey = itemId.ToString("N"),
            PlayCount = playCount,
            Played = playCount > 0,
            IsFavorite = isFavorite,
            PlaybackPositionTicks = playbackPositionTicks,
            Item = null!,
            User = null!
        });
}

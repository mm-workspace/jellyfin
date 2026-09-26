using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Covers which users' data rows an item query brings back. An item query joins the user data table alongside
/// the images and the linked children, so every user's rows multiply the rows the one statement returns. A query
/// made on behalf of a user only ever has its own user's rows read back, so it asks for those alone, while the
/// item a single retrieval returns is cached and read back for whichever user asks and keeps all of them.
/// </summary>
public sealed class BaseItemRepositoryUserDataScopeTests : DbTestFixture
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    private readonly CommandRecorder _recorder;
    private readonly BaseItemRepository _repository;
    private readonly User[] _users =
    [
        new("first", "auth-provider", "reset-provider"),
        new("second", "auth-provider", "reset-provider"),
        new("third", "auth-provider", "reset-provider"),
        new("fourth", "auth-provider", "reset-provider"),
        new("fifth", "auth-provider", "reset-provider")
    ];

    private readonly Guid _watchedByEveryone = Guid.NewGuid();
    private readonly Guid _watchedByTheOthersOnly = Guid.NewGuid();
    private readonly Guid _watchedByNobody = Guid.NewGuid();

    public BaseItemRepositoryUserDataScopeTests()
        : this(new CommandRecorder())
    {
    }

    private BaseItemRepositoryUserDataScopeTests(CommandRecorder recorder)
        : base(recorder)
    {
        _recorder = recorder;
        using (var context = CreateDbContext())
        {
            Seed(context);
        }

        _repository = CreateBaseItemRepository(new ItemTypeLookup());
    }

    [Fact]
    public void GetItemList_ForAUser_MapsThatUsersDataOnly()
    {
        var item = Assert.Single(MoviesFor(_users[0]), i => i.Id.Equals(_watchedByEveryone));

        var userData = Assert.Single(item.UserData);
        Assert.Equal(_users[0].Id, userData.UserId);
        Assert.Equal(1, userData.PlayCount);
    }

    [Fact]
    public void GetItemList_ForAUserWithoutARow_MapsNoData()
    {
        var item = Assert.Single(MoviesFor(_users[0]), i => i.Id.Equals(_watchedByTheOthersOnly));

        Assert.Empty(item.UserData);
    }

    [Fact]
    public void GetItemList_ForAUser_ReadsTheItemsInOneCommand()
    {
        _recorder.Commands.Clear();

        MoviesFor(_users[0]);

        Assert.Single(_recorder.Commands);
    }

    [Fact]
    public void GetItemList_ForAUser_ReadsOneRowPerItem()
    {
        _recorder.RowsRead.Clear();

        var items = MoviesFor(_users[0]);

        // Without the scope the join returns a row per item and user, so the three movies cost ten rows.
        Assert.Equal(items.Count, Assert.Single(_recorder.RowsRead));
    }

    [Fact]
    public void GetItemList_WithoutAUser_MapsEveryUsersData()
    {
        var items = _repository.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie]
        });

        var item = Assert.Single(items, i => i.Id.Equals(_watchedByEveryone));
        Assert.Equal(_users.Select(u => u.Id).OrderBy(id => id), item.UserData.Select(d => d.UserId).OrderBy(id => id));
    }

    [Fact]
    public void RetrieveItem_MapsEveryUsersData()
    {
        var item = _repository.RetrieveItem(_watchedByEveryone);

        Assert.NotNull(item);
        Assert.Equal(_users.Select(u => u.Id).OrderBy(id => id), item.UserData.Select(d => d.UserId).OrderBy(id => id));
        Assert.Equal(Enumerable.Range(1, _users.Length), item.UserData.Select(d => d.PlayCount).OrderBy(count => count));
    }

    [Fact]
    public void RetrieveItem_ReadsUserDataInACommandOfItsOwn()
    {
        _recorder.Commands.Clear();

        _repository.RetrieveItem(_watchedByEveryone);

        Assert.Equal(2, _recorder.Commands.Count);
        Assert.DoesNotContain("UserData", _recorder.Commands[0], StringComparison.Ordinal);
        Assert.Contains("UserData", _recorder.Commands[1], StringComparison.Ordinal);
    }

    [Fact]
    public void RetrieveItem_OfAnItemNobodyWatched_MapsNoData()
    {
        var item = _repository.RetrieveItem(_watchedByNobody);

        Assert.NotNull(item);
        Assert.Empty(item.UserData);
    }

    private IReadOnlyList<BaseItem> MoviesFor(User user)
        => _repository.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie]
        });

    private void Seed(JellyfinDbContext context)
    {
        context.Users.AddRange(_users);

        AddMovie(context, _watchedByEveryone, "A watched by everyone");
        AddMovie(context, _watchedByTheOthersOnly, "B watched by the others");
        AddMovie(context, _watchedByNobody, "C watched by nobody");

        // A distinct play count per user, so a row mapped for the wrong user is visible as a value and not
        // only as a count.
        for (var i = 0; i < _users.Length; i++)
        {
            AddUserData(context, _users[i], _watchedByEveryone, i + 1);
            if (i > 0)
            {
                AddUserData(context, _users[i], _watchedByTheOthersOnly, i + 1);
            }
        }

        context.SaveChanges();
    }

    private static void AddMovie(JellyfinDbContext context, Guid id, string name)
        => context.BaseItems.Add(new BaseItemEntity { Id = id, Type = MovieType, Name = name, SortName = name, PresentationUniqueKey = id.ToString("N") });

    private static void AddUserData(JellyfinDbContext context, User user, Guid itemId, int playCount)
        => context.UserData.Add(new UserData
        {
            ItemId = itemId,
            UserId = user.Id,
            CustomDataKey = itemId.ToString("N"),
            PlayCount = playCount,
            Played = true,
            Item = null!,
            User = null!
        });

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public List<int> RowsRead { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            // The last read is the one that reports the end of the result set.
            RowsRead.Add(eventData.ReadCount - 1);
            return result;
        }
    }
}

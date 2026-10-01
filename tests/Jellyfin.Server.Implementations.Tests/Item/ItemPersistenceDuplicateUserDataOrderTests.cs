using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// A batch delete cannot move two user data rows that share a user and a key onto the placeholder item, because
/// that is its primary key, so it keeps one of them and deletes the rest. Which row it keeps, and the order it
/// deletes the rest in, must follow from the rows themselves: the same library on two servers is the same data
/// stored in a different order and queried through different plans, and a delete order that came out of a read
/// with no ORDER BY is a delete order the two servers do not share.
/// </summary>
public sealed class ItemPersistenceDuplicateUserDataOrderTests : DbTestFixture
{
    private const string FirstKey = "key-1";
    private const string SecondKey = "key-2";

    // One group per key, each of two rows, and the two the delete removes belong to different items, so the
    // order it removes them in is not settled by the order within either group.
    private static readonly Guid _firstKept = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid _secondKept = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid _firstRemoved = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid _secondRemoved = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime _watched = new(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);

    private readonly DeletedRowRecorder _recorder;

    public ItemPersistenceDuplicateUserDataOrderTests()
        : this(new DeletedRowRecorder())
    {
    }

    private ItemPersistenceDuplicateUserDataOrderTests(DeletedRowRecorder recorder)
        : base(recorder)
    {
        _recorder = recorder;
    }

    [Fact]
    public async Task DeleteItem_DuplicateUserData_KeepsTheSameRowsWhicheverOrderTheRowsAreStored()
    {
        var stored = await RunDelete(reversed: false);
        var reversed = await RunDelete(reversed: true);

        Assert.Equal(stored.Kept, reversed.Kept);

        // Not just equal to each other: the rows kept are the ones the keys name.
        Assert.Equal([(FirstKey, 11), (SecondKey, 22)], stored.Kept);
    }

    [Fact]
    public async Task DeleteItem_DuplicateUserData_DeletesThemInTheSameOrderWhicheverOrderTheRowsAreStored()
    {
        var stored = await RunDelete(reversed: false);
        var reversed = await RunDelete(reversed: true);

        Assert.Equal(stored.Deleted, reversed.Deleted);

        // Not just equal to each other: the order is the one the rows are keyed in.
        Assert.Equal([(_firstRemoved, FirstKey), (_secondRemoved, SecondKey)], stored.Deleted);
    }

    [Fact]
    public void DeleteItem_DuplicateUserData_KeepsTheFurthestWatchedRow()
    {
        // The row kept is ranked the way the rest of the codebase settles conflicting user data, so the viewing
        // that got furthest survives the batch. The item id here would keep the other one, which is what makes
        // this a test of the ranking and not of the tie below it.
        (Guid ItemId, string Key, int Mark, long Ticks)[] rows =
        [
            (_firstKept, FirstKey, 11, 5),
            (_firstRemoved, FirstKey, 31, 50)
        ];

        Seed(rows);

        CreateService().DeleteItem([_firstKept, _firstRemoved]);

        using var context = CreateDbContext();
        var kept = context.UserData
            .Where(e => e.ItemId.Equals(BaseItemRepository.PlaceholderId))
            .Select(e => new { e.AudioStreamIndex, e.PlaybackPositionTicks })
            .AsEnumerable()
            .Select(e => (e.AudioStreamIndex ?? 0, e.PlaybackPositionTicks))
            .ToList();

        Assert.Equal([(31, 50L)], kept);
    }

    [Fact]
    public void SaveChanges_UserDataHandedOverBackToFront_WritesTheRowsInKeyOrder()
    {
        // Why nothing else in the item write paths needs an order of its own: everything they write through the
        // change tracker is written in key order whatever order it was handed over, so two servers saving the
        // same items in different orders still take the row locks in the same one.
        (Guid ItemId, string Key, int Mark, long Ticks)[] rows =
        [
            (_firstKept, FirstKey, 11, 0),
            (_firstKept, SecondKey, 12, 0),
            (_firstRemoved, FirstKey, 31, 0),
            (_firstRemoved, SecondKey, 32, 0)
        ];

        Seed(rows);

        _recorder.Clear();
        using (var context = CreateDbContext())
        {
            context.UserData.RemoveRange(context.UserData
                .AsEnumerable()
                .OrderByDescending(e => e.ItemId)
                .ThenByDescending(e => e.CustomDataKey, StringComparer.Ordinal));
            context.SaveChanges();
        }

        Assert.Equal(
            [(_firstKept, FirstKey), (_firstKept, SecondKey), (_firstRemoved, FirstKey), (_firstRemoved, SecondKey)],
            _recorder.Deleted);
    }

    /// <summary>
    /// Stores the same four rows and deletes the same four items, either in the order written here or in the
    /// reverse of it. Reversing changes where the rows sit in the table and the order the ids reach the query,
    /// which is all that separates one server from another holding this library.
    /// </summary>
    /// <param name="reversed">Whether to store the rows and list the items back to front.</param>
    /// <returns>The rows the delete kept, and the rows it deleted in the order it deleted them.</returns>
    private async Task<(List<(string Key, int Mark)> Kept, List<(Guid ItemId, string Key)> Deleted)> RunDelete(bool reversed)
    {
        await Database.ResetAsync(TestContext.Current.CancellationToken);

        // Equal playback fields throughout, so nothing but the tie decides which row is kept: every key the
        // ranking reads holds the same value in both rows of a group. The mark is a column the ranking does not
        // read, and is only there to tell the kept rows apart after they are moved onto the placeholder.
        (Guid ItemId, string Key, int Mark, long Ticks)[] rows =
        [
            (_secondRemoved, SecondKey, 42, 0),
            (_secondKept, SecondKey, 22, 0),
            (_firstRemoved, FirstKey, 31, 0),
            (_firstKept, FirstKey, 11, 0)
        ];

        Guid[] items = [_firstKept, _secondKept, _firstRemoved, _secondRemoved];
        Seed(reversed ? [.. rows.Reverse()] : rows);

        _recorder.Clear();
        CreateService().DeleteItem(reversed ? [.. items.Reverse()] : items);

        using var context = CreateDbContext();
        var kept = context.UserData
            .Where(e => e.ItemId.Equals(BaseItemRepository.PlaceholderId))
            .Select(e => new { e.CustomDataKey, e.AudioStreamIndex })
            .AsEnumerable()
            .Select(e => (e.CustomDataKey, e.AudioStreamIndex ?? 0))
            .OrderBy(e => e.CustomDataKey, StringComparer.Ordinal)
            .ToList();

        return (kept, [.. _recorder.Deleted]);
    }

    private ItemPersistenceService CreateService() => new(
        CreateDbContextFactory(),
        Mock.Of<IServerApplicationHost>(),
        Database.Provider,
        NullLogger<ItemPersistenceService>.Instance);

    private void Seed((Guid ItemId, string Key, int Mark, long Ticks)[] rows)
    {
        using var context = CreateDbContext();
        context.Users.Add(new User("user", "auth-provider", "reset-provider") { Id = _userId });
        foreach (var id in rows.Select(e => e.ItemId).Distinct())
        {
            context.BaseItems.Add(new BaseItemEntity
            {
                Id = id,
                Type = new ItemTypeLookup().BaseItemKindNames[BaseItemKind.Movie],
                Name = "Movie"
            });
        }

        context.SaveChanges();

        // One row at a time, so the rows reach the table in the order they are listed: a save writes the whole
        // batch in key order whatever order it was handed.
        foreach (var row in rows)
        {
            using var rowContext = CreateDbContext();
            rowContext.UserData.Add(new UserData
            {
                ItemId = row.ItemId,
                Item = null,
                UserId = _userId,
                User = null,
                CustomDataKey = row.Key,
                LastPlayedDate = _watched,
                PlayCount = 4,
                PlaybackPositionTicks = row.Ticks,
                AudioStreamIndex = row.Mark,
                Played = true
            });
            rowContext.SaveChanges();
        }
    }

    /// <summary>
    /// Records the rows a statement named by their whole key, in the order it named them. The set-based deletes
    /// around them name their rows through a sub-select instead, and are not of interest here.
    /// </summary>
    private sealed class DeletedRowRecorder : DbCommandInterceptor
    {
        private static readonly Regex _keyColumn = new("\"(ItemId|UserId|CustomDataKey)\" = [@$]([A-Za-z0-9_]+)", RegexOptions.CultureInvariant);

        public List<(Guid ItemId, string Key)> Deleted { get; } = [];

        public void Clear() => Deleted.Clear();

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        private void Record(DbCommand command)
        {
            var text = command.CommandText;
            if (!text.Contains("DELETE FROM \"UserData\"", StringComparison.Ordinal))
            {
                return;
            }

            // One provider binds a Guid, the other the text it is stored as, the columns are named in an order
            // of the provider's own choosing, and a save may batch several statements into one command, so each
            // statement is read for which of its parameters holds what.
            var values = command.Parameters
                .Cast<DbParameter>()
                .ToDictionary(e => e.ParameterName.TrimStart('@', '$'), e => e.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);

            foreach (var statement in text.Split("DELETE FROM \"UserData\"", StringSplitOptions.None).Skip(1))
            {
                var named = _keyColumn.Matches(statement.Split(';', 2)[0])
                    .ToDictionary(e => e.Groups[1].Value, e => e.Groups[2].Value, StringComparer.Ordinal);

                if (named.Count == 3
                    && values.TryGetValue(named["ItemId"], out var itemId)
                    && values.TryGetValue(named["CustomDataKey"], out var key)
                    && Guid.TryParse(itemId, out var id))
                {
                    Deleted.Add((id, key));
                }
            }
        }
    }
}

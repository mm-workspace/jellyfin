using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Emby.Server.Implementations.Library.Validators;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Testing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

/// <summary>
/// Covers a second server on the same database writing mappings of the rows the people validation task is merging
/// away. Inside one server the write permit keeps writers out of a merge; a second server has a permit of its own,
/// and so does a writer that went ahead after waiting the permit out. PostgreSQL lets both transactions run at once,
/// which is what these interleavings need; SQLite's transactions take the database write lock at BEGIN, so the
/// second writer waits there instead.
/// </summary>
/// <remarks>
/// What the conditional removal keeps is a mapping that is committed by the time it reads. One interleaving is not
/// covered here and is not closed: a mapping the other server has written but not yet committed when the removal
/// reads is still lost. The condition is evaluated on the snapshot taken before the removal blocks on the lock that
/// the other transaction's foreign key check holds on the credit, and the cascade which follows the removal then
/// deletes the mapping that transaction went on to commit. Keeping it needs a lock on the duplicated credit that
/// conflicts with that foreign key check, which no provider-neutral query takes.
/// </remarks>
public sealed class PeopleValidationTaskCrossProcessMergeTests
{
    private const string PersonName = "Person A";
    private const string OtherPersonName = "Person B";
    private const string PersonType = "Actor";
    private const string Role = "Hero";
    private const string OtherRole = "Cameo";
    private const string RoleOfTheDuplicate = "Villain";
    private const int DefaultNamePartitionSize = 100;

    private static readonly Guid _itemId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid _otherItemId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly TimeSpan _rendezvousTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task ExecuteAsync_WhileAnotherServerCreditsTheRowsBeingMerged_KeepsWhatItWrote()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = CreatePostgreSqlDatabase();
        AddMovie(database, _itemId, "Movie");
        AddMovie(database, _otherItemId, "Other Movie");

        // The two rows one name is stored in when two writers credit a person neither of them has stored yet.
        var firstCredit = AddCredit(database);
        var secondCredit = AddCredit(database);
        Map(database, _itemId, firstCredit, Role);

        using var lockOfThisServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        var reachedTheMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var theOtherServerCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = new PauseAfterTheMappingsWereMoved(reachedTheMove, theOtherServerCommitted.Task);
        var task = CreateTask(database, lockOfThisServer, DefaultNamePartitionSize, pause);

        var validating = Task.Run(() => task.ExecuteAsync(new Progress<double>(), cancellationToken), cancellationToken);
        try
        {
            var first = await Task.WhenAny(reachedTheMove.Task, validating, Task.Delay(_rendezvousTimeout, cancellationToken));
            if (first == validating)
            {
                // Rethrows what it failed with, instead of reporting that it never reached the move.
                await validating;
            }

            Assert.True(first == reachedTheMove.Task, "The task did not reach the statement that moves the mappings of a duplicated credit.");

            // The other server credits another item to both rows, on a connection that does not queue with the
            // write permit of this one. Whichever row this run is merging away, the mapping that names it was
            // written after the move and is the one the removal that follows would take with it.
            Map(database, _otherItemId, firstCredit, OtherRole);
            Map(database, _otherItemId, secondCredit, OtherRole);
        }
        finally
        {
            theOtherServerCommitted.TrySetResult();
        }

        // Rethrows what the run that was held up failed with.
        await validating;

        using var context = database.CreateDbContext();
        Assert.Equal(2, context.PeopleBaseItemMap.Count(e => e.ItemId.Equals(_otherItemId)));
        Assert.Single(context.PeopleBaseItemMap.Where(e => e.ItemId.Equals(_itemId)));

        // The row that kept a mapping is still there; the name is collapsed by the next run, which sees it.
        Assert.Equal(2, context.Peoples.Count());
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task ExecuteAsync_WhileAnotherServerCreditsTheKeptRowForTheRoleBeingMoved_CollapsesTheNameAnyway()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = CreatePostgreSqlDatabase();
        AddMovie(database, _itemId, "Movie");

        // Neither mapping is a duplicate of the other, so the statement that clears the way for the move finds
        // nothing to remove, whichever row the merge keeps.
        var firstCredit = AddCredit(database);
        var secondCredit = AddCredit(database);
        Map(database, _itemId, firstCredit, Role);
        Map(database, _itemId, secondCredit, RoleOfTheDuplicate);

        using var lockOfThisServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        var reachedTheMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var theOtherServerCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = new PauseAfterTheCollidingMappingsWereRemoved(reachedTheMove, theOtherServerCommitted.Task);
        var task = CreateTask(database, lockOfThisServer, DefaultNamePartitionSize, pause);

        var validating = Task.Run(() => task.ExecuteAsync(new Progress<double>(), cancellationToken), cancellationToken);
        try
        {
            var first = await Task.WhenAny(reachedTheMove.Task, validating, Task.Delay(_rendezvousTimeout, cancellationToken));
            if (first == validating)
            {
                // Rethrows what it failed with, instead of reporting that it never reached the statement.
                await validating;
            }

            Assert.True(first == reachedTheMove.Task, "The task did not reach the statement that removes the mappings which would collide.");

            // Both rows are now credited for the role the other one holds, so the move that follows meets a row
            // under the key (ItemId, PeopleId, Role) whichever row this run is merging away.
            Map(database, _itemId, firstCredit, RoleOfTheDuplicate);
            Map(database, _itemId, secondCredit, Role);
        }
        finally
        {
            theOtherServerCommitted.TrySetResult();
        }

        // The rejected move rolled its transaction back, and the merge ran again on a database where the mapping it
        // collided with is one to remove rather than one to move, so the run finishes instead of stopping here.
        await validating;

        using var context = database.CreateDbContext();
        var credit = Assert.Single(context.Peoples);
        Assert.Equal(2, context.PeopleBaseItemMap.Count());
        Assert.All(context.PeopleBaseItemMap, map => Assert.Equal(credit.Id, map.PeopleId));
        Assert.Single(context.PeopleBaseItemMap.Where(e => e.Role == Role));
        Assert.Single(context.PeopleBaseItemMap.Where(e => e.Role == RoleOfTheDuplicate));
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task ExecuteAsync_WithANameItCannotCollapse_MergesTheNamesBehindIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = CreatePostgreSqlDatabase();
        AddMovie(database, _itemId, "Movie");
        AddMovie(database, _otherItemId, "Other Movie");

        // The first name read is the one the other server writes to, so it is left behind; the second is behind it
        // in the ordering and collapses on its own.
        var firstCredit = AddCredit(database);
        var secondCredit = AddCredit(database);
        Map(database, _itemId, firstCredit, Role);
        var keptOfTheOtherName = AddCredit(database, OtherPersonName);
        var duplicateOfTheOtherName = AddCredit(database, OtherPersonName);
        Map(database, _itemId, keptOfTheOtherName, Role);
        Map(database, _otherItemId, duplicateOfTheOtherName, Role);

        using var lockOfThisServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        var reachedTheMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var theOtherServerCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = new PauseAfterTheMappingsWereMoved(reachedTheMove, theOtherServerCommitted.Task);

        // One name to a pass, so the name left behind is a pass that collapsed nothing and the name behind it is
        // read by the pass after it.
        var task = CreateTask(database, lockOfThisServer, 1, pause);

        var validating = Task.Run(() => task.ExecuteAsync(new Progress<double>(), cancellationToken), cancellationToken);
        try
        {
            var first = await Task.WhenAny(reachedTheMove.Task, validating, Task.Delay(_rendezvousTimeout, cancellationToken));
            if (first == validating)
            {
                // Rethrows what it failed with, instead of reporting that it never reached the move.
                await validating;
            }

            Assert.True(first == reachedTheMove.Task, "The task did not reach the statement that moves the mappings of a duplicated credit.");

            Map(database, _otherItemId, firstCredit, OtherRole);
            Map(database, _otherItemId, secondCredit, OtherRole);
        }
        finally
        {
            theOtherServerCommitted.TrySetResult();
        }

        await validating;

        using var context = database.CreateDbContext();

        // The name that was written to is still in two rows, and the name behind it is collapsed into one that
        // carries both of its mappings.
        Assert.Equal(2, context.Peoples.Count(e => e.Name == PersonName));
        var credit = Assert.Single(context.Peoples.Where(e => e.Name == OtherPersonName));
        Assert.Equal(2, context.PeopleBaseItemMap.Count(e => e.PeopleId.Equals(credit.Id)));
    }

    private static PostgreSqlTestDatabase CreatePostgreSqlDatabase()
    {
        var connectionString = TestDatabase.PostgreSqlConnectionString;
        Assert.SkipWhen(connectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
        return new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions
        {
            ApplicationPaths = Mock.Of<IApplicationPaths>()
        });
    }

    private static PeopleValidationTask CreateTask(PostgreSqlTestDatabase database, SerializedWriteLockBehavior writeLock, int namePartitionSize, params IInterceptor[] interceptors)
    {
        // The database was migrated without the locking behaviour, so its state is untouched when a test starts.
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>(database.Options);
        builder.AddInterceptors(interceptors);
        writeLock.Initialise(builder);
        var options = builder.Options;

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(e => e.GetPeopleNames(It.IsAny<InternalPeopleQuery>())).Returns([]);
        libraryManager.Setup(e => e.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([]);

        return new PeopleValidationTask(
            libraryManager.Object,
            Mock.Of<ILocalizationManager>(),
            new ContextFactory(() => new JellyfinDbContext(options, NullLogger<JellyfinDbContext>.Instance, database.Provider, writeLock)),
            Mock.Of<IFileSystem>(),
            NullLogger<PeopleValidationTask>.Instance,
            NullLogger<PeopleValidator>.Instance,
            new ItemTypeLookup(),
            database.Provider)
        {
            NamePartitionSize = namePartitionSize
        };
    }

    private static Guid AddCredit(PostgreSqlTestDatabase database, string name = PersonName)
    {
        var id = Guid.NewGuid();
        using var context = database.CreateDbContext();
        context.Peoples.Add(new People
        {
            Id = id,
            Name = name,
            PersonType = PersonType
        });
        context.SaveChanges();
        return id;
    }

    private static void Map(PostgreSqlTestDatabase database, Guid itemId, Guid creditId, string role)
    {
        using var context = database.CreateDbContext();
        context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
        {
            Item = null!,
            ItemId = itemId,
            People = null!,
            PeopleId = creditId,
            ListOrder = 0,
            SortOrder = 0,
            Role = role
        });
        context.SaveChanges();
    }

    private static void AddMovie(PostgreSqlTestDatabase database, Guid id, string name)
    {
        using var context = database.CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = new ItemTypeLookup().BaseItemKindNames[BaseItemKind.Movie],
            Name = name,
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false
        });
        context.SaveChanges();
    }

    private sealed class ContextFactory(Func<JellyfinDbContext> createDbContext) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => createDbContext();
    }

    /// <summary>
    /// Holds a merge where it has moved the mappings of a duplicated credit and is about to remove it, which is the
    /// window another server's write falls into, until that write has been committed.
    /// </summary>
    private sealed class PauseAfterTheMappingsWereMoved(TaskCompletionSource reached, Task release) : DbCommandInterceptor
    {
        private int _paused;

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("\"PeopleBaseItemMap\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                reached.SetResult();
                await Task.WhenAny(release, Task.Delay(_rendezvousTimeout, cancellationToken)).ConfigureAwait(false);
            }

            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds a merge where it has removed the mappings that would collide with the kept credit and is about to move
    /// the rest, which is the window another server's write falls into, until that write has been committed.
    /// </summary>
    private sealed class PauseAfterTheCollidingMappingsWereRemoved(TaskCompletionSource reached, Task release) : DbCommandInterceptor
    {
        private int _paused;

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE", StringComparison.Ordinal)
                && command.CommandText.Contains("\"PeopleBaseItemMap\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                reached.SetResult();
                await Task.WhenAny(release, Task.Delay(_rendezvousTimeout, cancellationToken)).ConfigureAwait(false);
            }

            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }
    }
}

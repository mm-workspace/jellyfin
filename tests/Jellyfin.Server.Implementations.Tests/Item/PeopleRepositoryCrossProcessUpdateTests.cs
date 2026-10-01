using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Covers two servers sharing one database writing the credits of the same item. Each has its own write lock, which
/// is what serializes the writers inside one server and what nothing does between them, and PostgreSQL lets both
/// transactions run at once, so one of them reads the mappings of the item before the other has written them and
/// looks the credit up after it has. That is the interleaving; SQLite cannot produce it, because its transactions
/// take the database write lock at BEGIN and the second writer waits there.
/// </summary>
public sealed class PeopleRepositoryCrossProcessUpdateTests
{
    private const string PersonName = "Person A";
    private const string Role = "Hero";
    private const string OtherPersonName = "Person B";
    private const string OtherRole = "Villain";

    private static readonly Guid _itemId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly TimeSpan _rendezvousTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task UpdatePeople_WhileAnotherServerWritesTheSameCredit_MapsTheCreditOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = CreatePostgreSqlDatabase();
        AddMovie(database);

        using var lockOfThisServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        using var lockOfTheOtherServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        var reachedTheLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var theOtherServerCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = new PauseAtTheCreditLookup(reachedTheLookup, theOtherServerCommitted.Task);

        var thisServer = CreateRepository(database, lockOfThisServer, pause);
        var theOtherServer = CreateRepository(database, lockOfTheOtherServer);

        var updating = Task.Run(() => thisServer.UpdatePeople(_itemId, [CreatePerson(PersonName, Role)]), cancellationToken);
        try
        {
            var first = await Task.WhenAny(reachedTheLookup.Task, updating, Task.Delay(_rendezvousTimeout, cancellationToken));
            if (first == updating)
            {
                // Rethrows what it failed with, instead of reporting that it never reached the lookup.
                await updating;
            }

            Assert.True(first == reachedTheLookup.Task, "The update did not reach the credit lookup, where the other server's write falls.");

            // Its transaction has read no mappings for the item; this one writes them and commits.
            theOtherServer.UpdatePeople(_itemId, [CreatePerson(PersonName, Role)]);
        }
        finally
        {
            theOtherServerCommitted.TrySetResult();
        }

        // Rethrows what the update that was held up failed with.
        await updating;

        using var context = database.CreateDbContext();
        Assert.Single(context.Peoples);
        var map = Assert.Single(context.PeopleBaseItemMap);
        Assert.Equal(Role, map.Role);
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task UpdatePeople_WhileAnotherServerGivesTheItemADifferentCast_StoresTheCastItWasGiven()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = CreatePostgreSqlDatabase();
        AddMovie(database);

        using var lockOfThisServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        using var lockOfTheOtherServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        var reachedTheLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var theOtherServerCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = new PauseAtTheCreditLookup(reachedTheLookup, theOtherServerCommitted.Task);

        var thisServer = CreateRepository(database, lockOfThisServer, pause);
        var theOtherServer = CreateRepository(database, lockOfTheOtherServer);

        // The cast both servers are about to replace, each with a different one of its own.
        theOtherServer.UpdatePeople(_itemId, [CreatePerson("Person X", "Narrator")]);

        var updating = Task.Run(() => thisServer.UpdatePeople(_itemId, [CreatePerson(PersonName, Role)]), cancellationToken);
        try
        {
            var first = await Task.WhenAny(reachedTheLookup.Task, updating, Task.Delay(_rendezvousTimeout, cancellationToken));
            if (first == updating)
            {
                // Rethrows what it failed with, instead of reporting that it never reached the lookup.
                await updating;
            }

            Assert.True(first == reachedTheLookup.Task, "The update did not reach the credit lookup, where the other server's write falls.");

            // Its transaction has read the mapping both servers mean to drop; this one drops it and commits, so
            // the row the held-up update is about to delete is no longer there when its delete looks for it.
            theOtherServer.UpdatePeople(_itemId, [CreatePerson(OtherPersonName, OtherRole)]);
        }
        finally
        {
            theOtherServerCommitted.TrySetResult();
        }

        // Rethrows what the update that was held up failed with.
        await updating;

        using var context = database.CreateDbContext();
        var credit = Assert.Single(context.Peoples);
        Assert.Equal(PersonName, credit.Name);
        var map = Assert.Single(context.PeopleBaseItemMap);
        Assert.Equal(Role, map.Role);
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

    private static PersonInfo CreatePerson(string name, string role) => new PersonInfo
    {
        Name = name,
        Type = PersonKind.Actor,
        Role = role
    };

    private static void AddMovie(PostgreSqlTestDatabase database)
    {
        using var context = database.CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = _itemId,
            Type = new ItemTypeLookup().BaseItemKindNames[BaseItemKind.Movie],
            Name = "Movie",
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false
        });
        context.SaveChanges();
    }

    private static PeopleRepository CreateRepository(PostgreSqlTestDatabase database, SerializedWriteLockBehavior writeLock, params IInterceptor[] interceptors)
    {
        // The database was migrated without the locking behaviour, so its state is untouched when a test starts.
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>(database.Options);
        builder.AddInterceptors(interceptors);
        writeLock.Initialise(builder);
        var options = builder.Options;

        return new PeopleRepository(
            new ContextFactory(() => new JellyfinDbContext(options, NullLogger<JellyfinDbContext>.Instance, database.Provider, writeLock)),
            new ItemTypeLookup(),
            Mock.Of<IItemQueryHelpers>(),
            database.Provider);
    }

    private sealed class ContextFactory(Func<JellyfinDbContext> createDbContext) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => createDbContext();
    }

    /// <summary>
    /// Holds an update where it has read the mappings of the item and is about to look up the credit they name,
    /// which is the window another server's write falls into, until that write has been committed.
    /// </summary>
    private sealed class PauseAtTheCreditLookup(TaskCompletionSource reached, Task release) : DbCommandInterceptor
    {
        private int _paused;

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.Transaction is not null
                && command.CommandText.Contains("FROM \"Peoples\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                reached.SetResult();
                release.Wait(_rendezvousTimeout);
            }

            return base.ReaderExecuting(command, eventData, result);
        }
    }
}

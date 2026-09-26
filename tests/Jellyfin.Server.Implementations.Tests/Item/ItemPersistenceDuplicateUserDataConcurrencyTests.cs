using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// A batch delete removes the user data rows a shared key leaves over one statement at a time, so the order of
/// that set is the order it takes their row locks in. Another writer reaching the same rows in a different order
/// is the one way two transactions can each hold what the other still needs: PostgreSQL runs them both and breaks
/// the cycle by aborting one of them, which is a failure the delete cannot tell from a write that did commit. The
/// order to take is the one the rows are keyed in, because it follows from the rows alone, and it is also what a
/// save batch and SQLite's own plan for this read hand over; only PostgreSQL reads them in the order they happen
/// to sit in the table.
/// </summary>
public sealed class ItemPersistenceDuplicateUserDataConcurrencyTests
{
    private const string SharedKey = "shared-key";
    private const int ItemCount = 6;
    private const int SeededPlayCount = 4;

    private static readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime _watched = new(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan _rendezvousTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The rows both writers reach. By the time the other server starts, the delete holds two of the set it is
    /// removing, and these three are placed so that one of them is among those two whichever end the delete
    /// started from, and the other server takes one the delete has yet to reach.
    /// </summary>
    private static readonly Guid[] _contested = [ItemId(1), ItemId(2), ItemId(3)];

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task DeleteItem_WhileAnotherServerDeletesTheSameRows_TakesTheRowsInTheSameOrder()
    {
        var connectionString = TestDatabase.PostgreSqlConnectionString;
        Assert.SkipWhen(connectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
        using var database = new PostgreSqlTestDatabase(connectionString!, new TestDatabaseOptions
        {
            ApplicationPaths = Mock.Of<IApplicationPaths>()
        });

        // Back to front, so the rows sit in the table in the reverse of the order they are keyed in. That is
        // what a library of alternate versions looks like once its rows have been rewritten a few times, and it
        // is the layout under which a read with no ORDER BY hands them over backwards.
        SeedReversed(database.CreateDbContext);

        using var lockOfThisServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        using var lockOfTheOtherServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        var reachedTheSecondDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var theOtherServerIsWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = new PauseAfterTheSecondRowDelete(reachedTheSecondDelete, theOtherServerIsWaiting.Task);

        var thisServer = CreateService(() => CreateContext(database, lockOfThisServer, pause), database.Provider);
        var cancellationToken = TestContext.Current.CancellationToken;
        var deleting = Task.Run(() => thisServer.DeleteItem(AllItemIds()), cancellationToken);
        var theOtherServerDeleting = Task.CompletedTask;
        try
        {
            var first = await Task.WhenAny(reachedTheSecondDelete.Task, deleting, Task.Delay(_rendezvousTimeout, cancellationToken));
            if (first == deleting)
            {
                // Rethrows what it failed with, instead of reporting that it never reached the second delete.
                await deleting;
            }

            Assert.True(first == reachedTheSecondDelete.Task, "The delete did not reach the second of its row deletes, which is where the other server starts.");

            theOtherServerDeleting = Task.Run(
                () => DeleteContestedRowsInKeyOrder(() => CreateContext(database, lockOfTheOtherServer)),
                cancellationToken);

            // The other server is only in the way once it holds a row this delete still needs, or needs one this
            // delete already holds. Waiting for it to be blocked is what makes that so on every run, rather than
            // hoping the two threads arrive in the right order.
            await WaitUntilABackendWaitsForALock(database, cancellationToken);
        }
        finally
        {
            theOtherServerIsWaiting.TrySetResult();
        }

        // Rethrows what either side failed with: a deadlock aborts one of the two, and the database reports it
        // to whichever it picked.
        await deleting;
        await theOtherServerDeleting;

        using var context = database.CreateDbContext();
        Assert.Empty(context.BaseItems.Where(e => e.Name == "Movie"));
        AssertOneRowSurvivedOnThePlaceholder(context);
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task SetBasedWrite_WithTheIdsInTheOppositeOrder_QueuesInsteadOfDeadlocking()
    {
        // Why the statements a batch delete issues per table need no order of their own, and why sorting the ids
        // handed to them would buy nothing: a set-based statement takes its row locks in the order its plan
        // visits the rows, which is the order they sit in the table, not the order of the ids in the parameter.
        // Were it the parameter's order, two writers going opposite ways would hold what the other needs.
        var connectionString = TestDatabase.PostgreSqlConnectionString;
        Assert.SkipWhen(connectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
        using var database = new PostgreSqlTestDatabase(connectionString!, new TestDatabaseOptions
        {
            ApplicationPaths = Mock.Of<IApplicationPaths>()
        });

        SeedReversed(database.CreateDbContext);
        var ascending = AllItemIds();
        var descending = ascending.Reverse().ToArray();

        const int Rounds = 6;
        for (var round = 0; round < Rounds; round++)
        {
            using var bothAreIn = new Barrier(2);
            var cancellationToken = TestContext.Current.CancellationToken;
            await Task.WhenAll(
                Task.Run(() => TouchTheRows(database, ascending, bothAreIn), cancellationToken),
                Task.Run(() => TouchTheRows(database, descending, bothAreIn), cancellationToken));
        }

        // Both writers committed every round, on every row: a deadlock would have thrown out of the round that
        // hit it, and a statement that quietly matched nothing would leave the counts short.
        using var context = database.CreateDbContext();
        var counts = context.UserData.Select(e => e.PlayCount).ToList();
        Assert.Equal(ItemCount, counts.Count);
        Assert.All(counts, count => Assert.Equal(SeededPlayCount + (2 * Rounds), count));
    }

    [Fact]
    [Trait("Provider", "Sqlite")]
    public async Task DeleteItem_WhileAnotherServerDeletesTheSameRows_IsNotInterleavedWithIt()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "jellyfin-duplicate-user-data-" + Guid.NewGuid().ToString("N") + ".db");
        using var lockOfThisServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        using var lockOfTheOtherServer = new SerializedWriteLockBehavior(NullLogger<SerializedWriteLockBehavior>.Instance);
        try
        {
            var schema = CreateSqliteContext(databasePath, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
            await using (schema.ConfigureAwait(false))
            {
                await schema.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }

            SeedReversed(() => CreateSqliteContext(databasePath, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)));

            var order = new OrderOfEvents();
            var reachedTheSecondDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var theOtherServerHasBegun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pause = new PauseAfterTheSecondRowDelete(reachedTheSecondDelete, theOtherServerHasBegun.Task);
            var recordCommit = new CommitRecorder(() => order.Add("the delete committed"));

            var thisServer = CreateService(
                () => CreateSqliteContext(databasePath, lockOfThisServer, pause, recordCommit),
                new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance));

            var cancellationToken = TestContext.Current.CancellationToken;
            var deleting = Task.Run(() => thisServer.DeleteItem(AllItemIds()), cancellationToken);
            var theOtherServerDeleting = Task.CompletedTask;
            try
            {
                var first = await Task.WhenAny(reachedTheSecondDelete.Task, deleting, Task.Delay(_rendezvousTimeout, cancellationToken));
                if (first == deleting)
                {
                    await deleting;
                }

                Assert.True(first == reachedTheSecondDelete.Task, "The delete did not reach the second of its row deletes, which is where the other server starts.");

                var announceTheStart = new TransactionStartRecorder(() => theOtherServerHasBegun.TrySetResult());
                var recordTheFirstStatement = new FirstRowDeleteRecorder(() => order.Add("the other server's first row delete"));
                theOtherServerDeleting = Task.Run(
                    () => DeleteContestedRowsInKeyOrder(() => CreateSqliteContext(databasePath, lockOfTheOtherServer, announceTheStart, recordTheFirstStatement)),
                    cancellationToken);

                await theOtherServerHasBegun.Task.WaitAsync(_rendezvousTimeout, cancellationToken);
            }
            finally
            {
                theOtherServerHasBegun.TrySetResult();
            }

            await deleting;
            await theOtherServerDeleting;

            // The other server queued for the whole of the delete's transaction, so the two orders never met and
            // the interleaving the PostgreSQL test needs cannot arise here at all. Each server holds a write
            // permit of its own, so what keeps them apart is the database write lock SQLite takes at BEGIN
            // IMMEDIATE, which it holds until the delete commits.
            Assert.Equal(["the delete committed", "the other server's first row delete"], order.Events);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static Guid ItemId(int index) =>
        Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"cccccccc-0000-0000-0000-{index:D12}"));

    private static Guid[] AllItemIds() => [.. Enumerable.Range(0, ItemCount).Select(ItemId)];

    private static ItemPersistenceService CreateService(Func<JellyfinDbContext> createContext, IJellyfinDatabaseProvider provider) => new(
        new ContextFactory(createContext),
        Mock.Of<IServerApplicationHost>(),
        provider,
        NullLogger<ItemPersistenceService>.Instance);

    private static JellyfinDbContext CreateContext(PostgreSqlTestDatabase database, SerializedWriteLockBehavior writeLock, params IInterceptor[] interceptors)
    {
        // The database was migrated without the locking behaviour, so its state is untouched when a test starts.
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>(database.Options);

        // Added first, so they run before the behaviour's interceptors.
        builder.AddInterceptors(interceptors);
        writeLock.Initialise(builder);
        return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, database.Provider, writeLock);
    }

    private static JellyfinDbContext CreateSqliteContext(string databasePath, IEntityFrameworkCoreLockingBehavior behavior, params IInterceptor[] interceptors)
    {
        var provider = new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite($"Data Source={databasePath};Pooling=false");
        builder.AddInterceptors(interceptors);
        behavior.Initialise(builder);
        return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, provider, behavior);
    }

    /// <summary>
    /// Stores one item per id and one user data row per item under the same key, the rows back to front so that
    /// the order they sit in the table is the reverse of the order they are keyed in.
    /// </summary>
    /// <param name="createContext">Creates a context on the database to seed.</param>
    private static void SeedReversed(Func<JellyfinDbContext> createContext)
    {
        using (var context = createContext())
        {
            context.Users.Add(new User("user", "auth-provider", "reset-provider") { Id = _userId });
            foreach (var id in AllItemIds())
            {
                context.BaseItems.Add(new BaseItemEntity
                {
                    Id = id,
                    Type = new ItemTypeLookup().BaseItemKindNames[BaseItemKind.Movie],
                    Name = "Movie"
                });
            }

            context.SaveChanges();
        }

        // One row per transaction: a save writes a whole batch in key order whatever order it was handed.
        foreach (var id in AllItemIds().Reverse())
        {
            using var context = createContext();
            context.UserData.Add(new UserData
            {
                ItemId = id,
                Item = null,
                UserId = _userId,
                User = null,
                CustomDataKey = SharedKey,
                LastPlayedDate = _watched,
                PlayCount = SeededPlayCount,
                PlaybackPositionTicks = 7,
                Played = true
            });
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Deletes the contested rows in the order they are keyed in, one statement each, as a save batch and
    /// SQLite's own plan for the delete's read both do. A statement per row, so that finding a row already gone
    /// is the no-op it is rather than a reported lost update, which is not what this covers.
    /// </summary>
    /// <param name="createContext">Creates a context on the database to write to.</param>
    private static void DeleteContestedRowsInKeyOrder(Func<JellyfinDbContext> createContext)
    {
        using var context = createContext();
        using var transaction = context.Database.BeginTransaction();
        foreach (var itemId in _contested)
        {
            context.UserData
                .Where(e => e.ItemId.Equals(itemId) && e.UserId.Equals(_userId) && e.CustomDataKey == SharedKey)
                .ExecuteDelete();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Writes every one of the rows in one statement, the ids bound as one collection parameter in the order
    /// given, as the batch delete does when it moves the rows it keeps onto the placeholder.
    /// </summary>
    /// <param name="database">The database to write to.</param>
    /// <param name="ids">The ids to name, in the order to name them.</param>
    /// <param name="bothAreIn">Released once both writers are about to issue their statement.</param>
    private static void TouchTheRows(PostgreSqlTestDatabase database, Guid[] ids, Barrier bothAreIn)
    {
        using var context = database.CreateDbContext();
        using var transaction = context.Database.BeginTransaction();
        bothAreIn.SignalAndWait(_rendezvousTimeout);
        context.UserData
            .WhereOneOrMany(ids, e => e.ItemId)
            .ExecuteUpdate(e => e.SetProperty(f => f.PlayCount, f => f.PlayCount + 1));
        transaction.Commit();
    }

    private static void AssertOneRowSurvivedOnThePlaceholder(JellyfinDbContext context)
    {
        // Whichever row was kept, the other server deleted three of the rest and the delete the remaining ones.
        Assert.Empty(context.UserData.Where(e => e.CustomDataKey == SharedKey && !e.ItemId.Equals(BaseItemRepository.PlaceholderId)));
        Assert.Single(context.UserData.Where(e => e.ItemId.Equals(BaseItemRepository.PlaceholderId) && e.CustomDataKey == SharedKey));
    }

    private static async Task WaitUntilABackendWaitsForALock(PostgreSqlTestDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'",
            connection);

        using var giveUp = new CancellationTokenSource(_rendezvousTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, giveUp.Token);
        while (true)
        {
            var waiting = (long)(await command.ExecuteScalarAsync(linked.Token).ConfigureAwait(false))!;
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a statement as a delete of one user data row by its whole key, which is what both writers here
    /// issue. The set-based deletes around them name their rows through a sub-select instead.
    /// </summary>
    private static bool IsARowDelete(DbCommand command) =>
        command.CommandText.Contains("DELETE FROM \"UserData\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"CustomDataKey\"", StringComparison.Ordinal)
        && !command.CommandText.Contains("SELECT", StringComparison.Ordinal);

    private sealed class ContextFactory(Func<JellyfinDbContext> createDbContext) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => createDbContext();
    }

    /// <summary>
    /// Records what happened in the order it happened.
    /// </summary>
    private sealed class OrderOfEvents
    {
        private readonly List<string> _events = [];

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Add(string what)
        {
            lock (_events)
            {
                _events.Add(what);
            }
        }
    }

    /// <summary>
    /// Holds the delete where it has taken the row locks of the first two rows of the set it is removing, which
    /// is the point another writer of those rows can be in the way, until that writer is.
    /// </summary>
    private sealed class PauseAfterTheSecondRowDelete(TaskCompletionSource reached, Task release) : DbCommandInterceptor
    {
        private int _rowDeletes;

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            if (IsARowDelete(command) && Interlocked.Increment(ref _rowDeletes) == 2)
            {
                reached.SetResult();
                release.Wait(_rendezvousTimeout);
            }

            return base.NonQueryExecuted(command, eventData, result);
        }
    }

    /// <summary>
    /// Reports the first row delete a writer makes.
    /// </summary>
    private sealed class FirstRowDeleteRecorder(Action first) : DbCommandInterceptor
    {
        private int _reported;

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (IsARowDelete(command) && Interlocked.Exchange(ref _reported, 1) == 0)
            {
                first();
            }

            return base.NonQueryExecuting(command, eventData, result);
        }
    }

    /// <summary>
    /// Reports a committed transaction.
    /// </summary>
    private sealed class CommitRecorder(Action committed) : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            committed();
            base.TransactionCommitted(transaction, eventData);
        }
    }

    /// <summary>
    /// Signals before a transaction asks for the write permit, which the behaviour's own interceptor does next.
    /// </summary>
    private sealed class TransactionStartRecorder(Action starting) : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            starting();
            return base.TransactionStarting(connection, eventData, result);
        }
    }
}

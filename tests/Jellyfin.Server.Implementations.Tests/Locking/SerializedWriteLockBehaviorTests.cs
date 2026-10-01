using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Database.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Locking;

public sealed class SerializedWriteLockBehaviorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "jellyfin-serialized-writes-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly SerializedWriteLockBehavior _behavior = new(NullLogger<SerializedWriteLockBehavior>.Instance);
    private readonly DbContextOptions<JellyfinDbContext> _options;

    public SerializedWriteLockBehaviorTests()
    {
        // Create the schema without the behaviour under test, so its state is untouched when a test starts.
        var schemaOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=false").Options;
        using (var schemaContext = new JellyfinDbContext(schemaOptions, NullLogger<JellyfinDbContext>.Instance, new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance), new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)))
        {
            schemaContext.Database.EnsureCreated();
        }

        var builder = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=false");
        _behavior.Initialise(builder);
        _options = builder.Options;
    }

    [Fact]
    public async Task SaveChangesAsync_Uncontended_DoesNotWaitForItself()
    {
        await using var context = CreateContext();
        context.ActivityLogs.Add(new ActivityLog("one", "type", Guid.NewGuid()));
        context.ActivityLogs.Add(new ActivityLog("two", "type", Guid.NewGuid()));

        var stopwatch = Stopwatch.StartNew();
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"An uncontended SaveChangesAsync took {stopwatch.Elapsed.TotalSeconds:F1}s.");
    }

    [Fact]
    public void SaveChanges_Uncontended_DoesNotWaitForItself()
    {
        using var context = CreateContext();
        context.ActivityLogs.Add(new ActivityLog("one", "type", Guid.NewGuid()));
        context.ActivityLogs.Add(new ActivityLog("two", "type", Guid.NewGuid()));

        var stopwatch = Stopwatch.StartNew();
        context.SaveChanges();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"An uncontended SaveChanges took {stopwatch.Elapsed.TotalSeconds:F1}s.");
    }

    [Fact]
    public async Task ExplicitTransaction_AcrossAwaits_ReleasesOnCommit()
    {
        await using (var context = CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            context.ActivityLogs.Add(new ActivityLog("in transaction", "type", Guid.NewGuid()));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await Task.Yield();
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        await using (var context = CreateContext())
        {
            context.ActivityLogs.Add(new ActivityLog("after", "type", Guid.NewGuid()));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"A write after a committed transaction took {stopwatch.Elapsed.TotalSeconds:F1}s.");
    }

    [Fact]
    public async Task ConcurrentWriters_AfterASynchronousTransaction_NeverOverlap()
    {
        using (var context = CreateContext())
        {
            WriteInSynchronousTransaction(context);
        }

        await ConcurrentWriters_NeverOverlap();
    }

    [Fact]
    public async Task ConcurrentWriters_NeverOverlap()
    {
        var active = 0;
        var maxActive = 0;
        void Enter()
        {
            var now = Interlocked.Increment(ref active);
            int seen;
            while ((seen = Volatile.Read(ref maxActive)) < now && Interlocked.CompareExchange(ref maxActive, now, seen) != seen)
            {
            }
        }

        void Exit() => Interlocked.Decrement(ref active);

        var recorder = new ConcurrencyRecorder(Enter, Exit);

        var builder = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=false");
        _behavior.Initialise(builder);
        builder.AddInterceptors(recorder);
        var options = builder.Options;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await using var context = new JellyfinDbContext(options, NullLogger<JellyfinDbContext>.Instance, new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance), _behavior);
            context.ActivityLogs.Add(new ActivityLog("writer " + i, "type", Guid.NewGuid()));
            context.ActivityLogs.Add(new ActivityLog("writer " + i, "type", Guid.NewGuid()));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        })));

        Assert.Equal(1, maxActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginTransaction_WhileSaveChangesHoldsThePermit_WaitsForItBeforeLockingTheDatabase(bool beginAsync)
    {
        var logger = new RecordingLogger();
        using var behavior = new SerializedWriteLockBehavior(logger, TimeSpan.FromSeconds(30));
        var events = new ConcurrentQueue<string>();
        var permitHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beginning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // SaveChanges pauses with the permit held, before it begins its own transaction, until the explicit transaction
        // below has passed BEGIN. One that waits for the permit first never does, so the pause ends a second after
        // TransactionStarting, which BEGIN follows at once when nothing waits in between.
        var pause = new SaveChangesPause(
            async cancellationToken =>
            {
                permitHeld.SetResult();
                await beginning.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAny(begun.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false);
            },
            () => events.Enqueue("saved"));

        var recorder = new TransactionStartRecorder(
            () => beginning.TrySetResult(),
            () =>
            {
                events.Enqueue("begun");
                begun.TrySetResult();
            });

        async Task SaveAsync()
        {
            // A short busy timeout turns a wait for a database write lock the other side took first into "database is locked".
            await using var context = CreateContext(behavior, "Default Timeout=1", pause);
            context.ActivityLogs.Add(new ActivityLog("one", "type", Guid.NewGuid()));
            context.ActivityLogs.Add(new ActivityLog("two", "type", Guid.NewGuid()));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        async Task TransactAsync()
        {
            await using var context = CreateContext(behavior, string.Empty, recorder);
            if (beginAsync)
            {
                await WriteInTransactionAsync(context);
            }
            else
            {
                WriteInSynchronousTransaction(context);
            }
        }

        var saving = Task.Run(SaveAsync, TestContext.Current.CancellationToken);
        await permitHeld.Task.WaitAsync(TestContext.Current.CancellationToken);
        var transacting = Task.Run(TransactAsync, TestContext.Current.CancellationToken);

        await Task.WhenAll(saving, transacting);

        Assert.Equal(["saved", "begun"], events);
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginTransaction_WhenBeginFails_ReleasesThePermit(bool beginAsync)
    {
        var logger = new RecordingLogger();
        using var behavior = new SerializedWriteLockBehavior(logger, TimeSpan.FromSeconds(2));

        await using (var blocker = new SqliteConnection($"Data Source={_databasePath};Pooling=false"))
        {
            await blocker.OpenAsync(TestContext.Current.CancellationToken);

            // BEGIN IMMEDIATE: this connection holds the database write lock until it rolls back.
            await using var blocking = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using var context = CreateContext(behavior, "Default Timeout=1");

            var exception = beginAsync
                ? await Assert.ThrowsAsync<SqliteException>(() => context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
                : Assert.Throws<SqliteException>(() => BeginSynchronousTransaction(context));
            Assert.Equal(5, exception.SqliteErrorCode);
        }

        await using (var context = CreateContext(behavior))
        {
            context.ActivityLogs.Add(new ActivityLog("after", "type", Guid.NewGuid()));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionThatBeganWithoutThePermit_Writes_WithoutWaitingForItAgain(bool async)
    {
        var logger = new RecordingLogger();
        var acquireTimeout = TimeSpan.FromSeconds(1);
        using var behavior = new SerializedWriteLockBehavior(logger, acquireTimeout);
        var permitHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // SaveChanges holds the permit before it begins its own transaction, so the database stays unlocked until it resumes.
        var pause = new SaveChangesPause(
            async cancellationToken =>
            {
                permitHeld.SetResult();
                await resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
            () => { });

        async Task SaveAsync()
        {
            await using var context = CreateContext(behavior, string.Empty, pause);
            context.ActivityLogs.Add(new ActivityLog("paused", "type", Guid.NewGuid()));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var saving = Task.Run(SaveAsync, TestContext.Current.CancellationToken);
        await permitHeld.Task.WaitAsync(TestContext.Current.CancellationToken);

        TimeSpan writing;
        try
        {
            // BEGIN IMMEDIATE follows the timed out wait, and the transaction holds the database write lock from then on.
            // Waiting for the permit again, on each write, would take the two in the opposite order to SaveChanges.
            await using var context = CreateContext(behavior);
            writing = async
                ? await WriteTwiceInTransactionAsync(context)
                : WriteTwiceInSynchronousTransaction(context);
        }
        finally
        {
            resume.SetResult();
        }

        await saving;

        Assert.True(writing < acquireTimeout, $"Writing in a transaction that began without the permit took {writing.TotalSeconds:F1}s.");
        Assert.Single(logger.Warnings);
    }

    [Theory]
    [Trait("Provider", "PostgreSql")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlySnapshotTransaction_OnPostgreSql_DoesNotDelayWritesOnOtherContexts(bool async)
    {
        await using var database = CreatePostgreSqlDatabase();
        var logger = new RecordingLogger();
        using var behavior = new SerializedWriteLockBehavior(logger, TimeSpan.FromSeconds(2));

        // Like a full-system backup, which reads every table from one snapshot.
        await using var reader = CreateContext(database, behavior);
        if (async)
        {
            await using var transaction = await reader.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.Current.CancellationToken);
            await reader.ActivityLogs.CountAsync(TestContext.Current.CancellationToken);

            await using var writer = CreateContext(database, behavior);
            writer.ActivityLogs.Add(new ActivityLog("during a read", "type", Guid.NewGuid()));
            await writer.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            WriteDuringSynchronousReadOnlyTransaction(reader, database, behavior);
        }

        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [Trait("Provider", "PostgreSql")]
    [InlineData(IsolationLevel.Unspecified, nameof(DbContext.SaveChanges))]
    [InlineData(IsolationLevel.Unspecified, nameof(DbContext.SaveChangesAsync))]
    [InlineData(IsolationLevel.Unspecified, nameof(RelationalDatabaseFacadeExtensions.ExecuteSqlRaw))]
    [InlineData(IsolationLevel.Unspecified, nameof(EntityFrameworkQueryableExtensions.ExecuteUpdateAsync))]
    [InlineData(IsolationLevel.RepeatableRead, nameof(DbContext.SaveChanges))]
    [InlineData(IsolationLevel.RepeatableRead, nameof(DbContext.SaveChangesAsync))]
    [InlineData(IsolationLevel.RepeatableRead, nameof(RelationalDatabaseFacadeExtensions.ExecuteSqlRaw))]
    [InlineData(IsolationLevel.RepeatableRead, nameof(EntityFrameworkQueryableExtensions.ExecuteUpdateAsync))]
    public async Task TransactionThatHasWritten_OnPostgreSql_HoldsThePermitUntilCommit(IsolationLevel isolationLevel, string firstWrite)
    {
        await using var database = CreatePostgreSqlDatabase();
        var logger = new RecordingLogger();
        using var behavior = new SerializedWriteLockBehavior(logger, TimeSpan.FromSeconds(10));

        await using var owner = CreateContext(database, behavior);
        await using var transaction = await owner.Database.BeginTransactionAsync(isolationLevel, TestContext.Current.CancellationToken);
        switch (firstWrite)
        {
            case nameof(DbContext.SaveChangesAsync):
                owner.ActivityLogs.Add(new ActivityLog("first", "type", Guid.NewGuid()));
                await owner.SaveChangesAsync(TestContext.Current.CancellationToken);
                break;
            case nameof(EntityFrameworkQueryableExtensions.ExecuteUpdateAsync):
                await owner.ActivityLogs.ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, "updated"), TestContext.Current.CancellationToken);
                break;
            default:
                WriteSynchronously(owner, firstWrite);
                break;
        }

        async Task WriteElsewhereAsync()
        {
            await using var other = CreateContext(database, behavior);
            other.ActivityLogs.Add(new ActivityLog("other", "type", Guid.NewGuid()));
            await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var otherWrite = Task.Run(WriteElsewhereAsync, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.False(otherWrite.IsCompleted, "A write on another context went ahead while a transaction that had written was still open.");

        // The transaction's later writes run under the permit it holds, while the other write still waits for it.
        owner.ActivityLogs.Add(new ActivityLog("second", "type", Guid.NewGuid()));
        await owner.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.False(otherWrite.IsCompleted, "A write on another context went ahead while a transaction that had written was still open.");

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        await otherWrite;
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [Trait("Provider", "PostgreSql")]
    [InlineData(IsolationLevel.Unspecified)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task TransactionThatHasWritten_OnPostgreSql_ReleasesThePermitWhenDisposedWithoutCommit(IsolationLevel isolationLevel)
    {
        await using var database = CreatePostgreSqlDatabase();
        var logger = new RecordingLogger();
        using var behavior = new SerializedWriteLockBehavior(logger, TimeSpan.FromSeconds(2));

        await using (var owner = CreateContext(database, behavior))
        {
            await using var transaction = await owner.Database.BeginTransactionAsync(isolationLevel, TestContext.Current.CancellationToken);
            owner.ActivityLogs.Add(new ActivityLog("abandoned", "type", Guid.NewGuid()));
            await owner.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var other = CreateContext(database, behavior))
        {
            other.ActivityLogs.Add(new ActivityLog("after", "type", Guid.NewGuid()));
            await other.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["after"], await other.ActivityLogs.Select(e => e.Name).ToListAsync(TestContext.Current.CancellationToken));
        }

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task InsertIfMissing_InConcurrentTransactionsOnPostgreSql_InsertsOnce()
    {
        await using var database = CreatePostgreSqlDatabase();
        var logger = new RecordingLogger();
        using var behavior = new SerializedWriteLockBehavior(logger, TimeSpan.FromSeconds(10));
        var rendezvous = new CheckRendezvous();

        async Task InsertIfMissingAsync()
        {
            await using var context = CreateContext(database, behavior);
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var exists = await context.ActivityLogs.AnyAsync(e => e.Name == "shared", TestContext.Current.CancellationToken);
            await rendezvous.CheckedAsync(TestContext.Current.CancellationToken);
            if (!exists)
            {
                context.ActivityLogs.Add(new ActivityLog("shared", "type", Guid.NewGuid()));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(
            Task.Run(InsertIfMissingAsync, TestContext.Current.CancellationToken),
            Task.Run(InsertIfMissingAsync, TestContext.Current.CancellationToken));

        await using var check = CreateContext(database, behavior);
        Assert.Equal(1, await check.ActivityLogs.CountAsync(e => e.Name == "shared", TestContext.Current.CancellationToken));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task InsertItemValueIfMissing_InConcurrentTransactionsOnPostgreSql_DoesNotViolateItsUniqueIndex()
    {
        await using var database = CreatePostgreSqlDatabase();
        var logger = new RecordingLogger();
        using var behavior = new SerializedWriteLockBehavior(logger, TimeSpan.FromSeconds(10));
        var rendezvous = new CheckRendezvous();

        await Task.WhenAll(
            Task.Run(() => InsertItemValueIfMissing(database, behavior, rendezvous), TestContext.Current.CancellationToken),
            Task.Run(() => InsertItemValueIfMissing(database, behavior, rendezvous), TestContext.Current.CancellationToken));

        await using var check = CreateContext(database, behavior);
        Assert.Equal(1, await check.ItemValues.CountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(logger.Warnings);
    }

    public void Dispose()
    {
        _behavior.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    private static PostgreSqlTestDatabase CreatePostgreSqlDatabase()
    {
        var connectionString = TestDatabase.PostgreSqlConnectionString;
        Assert.SkipWhen(connectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
        return new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions());
    }

    private static JellyfinDbContext CreateContext(PostgreSqlTestDatabase database, SerializedWriteLockBehavior behavior)
    {
        // The database was migrated without the behaviour under test, so its state is untouched when a test starts.
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>(database.Options);
        behavior.Initialise(builder);
        return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, database.Provider, behavior);
    }

    private static async Task WriteInTransactionAsync(JellyfinDbContext context)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        context.ActivityLogs.Add(new ActivityLog("async", "type", Guid.NewGuid()));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<TimeSpan> WriteTwiceInTransactionAsync(JellyfinDbContext context)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        context.ActivityLogs.Add(new ActivityLog("async", "type", Guid.NewGuid()));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync("UPDATE \"ActivityLogs\" SET \"Name\" = 'updated'", TestContext.Current.CancellationToken);
        var elapsed = stopwatch.Elapsed;
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return elapsed;
    }

    // The synchronous code paths are what these cover.
#pragma warning disable CA1849
    private static void WriteInSynchronousTransaction(JellyfinDbContext context)
    {
        using var transaction = context.Database.BeginTransaction();
        context.ActivityLogs.Add(new ActivityLog("sync", "type", Guid.NewGuid()));
        context.SaveChanges();
        transaction.Commit();
    }

    private static TimeSpan WriteTwiceInSynchronousTransaction(JellyfinDbContext context)
    {
        using var transaction = context.Database.BeginTransaction();
        var stopwatch = Stopwatch.StartNew();
        context.ActivityLogs.Add(new ActivityLog("sync", "type", Guid.NewGuid()));
        context.SaveChanges();
        context.Database.ExecuteSqlRaw("UPDATE \"ActivityLogs\" SET \"Name\" = 'updated'");
        var elapsed = stopwatch.Elapsed;
        transaction.Commit();
        return elapsed;
    }

    private static void BeginSynchronousTransaction(JellyfinDbContext context)
    {
        using var transaction = context.Database.BeginTransaction();
    }

    /// <summary>
    /// Checks for a genre and inserts it if it is missing, in one transaction, as saving an item does.
    /// </summary>
    private static void InsertItemValueIfMissing(PostgreSqlTestDatabase database, SerializedWriteLockBehavior behavior, CheckRendezvous rendezvous)
    {
        using var context = CreateContext(database, behavior);
        using var transaction = context.Database.BeginTransaction();
        var exists = context.ItemValues.Any(e => e.Type == ItemValueType.Genre && e.Value == "Shared");
        rendezvous.Checked(TestContext.Current.CancellationToken);
        if (!exists)
        {
            context.ItemValues.Add(new ItemValue
            {
                ItemValueId = Guid.NewGuid(),
                Type = ItemValueType.Genre,
                Value = "Shared",
                CleanValue = "shared"
            });
            context.SaveChanges();
        }

        transaction.Commit();
    }

    private static void WriteDuringSynchronousReadOnlyTransaction(JellyfinDbContext reader, PostgreSqlTestDatabase database, SerializedWriteLockBehavior behavior)
    {
        using var transaction = reader.Database.BeginTransaction(IsolationLevel.RepeatableRead);
        _ = reader.ActivityLogs.Count();

        using (var writer = CreateContext(database, behavior))
        {
            writer.ActivityLogs.Add(new ActivityLog("during a read", "type", Guid.NewGuid()));
            writer.SaveChanges();
        }

        transaction.Commit();
    }

    private static void WriteSynchronously(JellyfinDbContext context, string write)
    {
        switch (write)
        {
            case nameof(DbContext.SaveChanges):
                context.ActivityLogs.Add(new ActivityLog("first", "type", Guid.NewGuid()));
                context.SaveChanges();
                break;
            case nameof(RelationalDatabaseFacadeExtensions.ExecuteSqlRaw):
                context.Database.ExecuteSqlRaw("UPDATE \"ActivityLogs\" SET \"Name\" = 'updated'");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(write), write, null);
        }
    }
#pragma warning restore CA1849

    private JellyfinDbContext CreateContext() => new(
        _options,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
        _behavior);

    private JellyfinDbContext CreateContext(SerializedWriteLockBehavior behavior, string connectionOptions = "", params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=false;{connectionOptions}");

        // Added first, so they run before the behaviour's interceptors.
        builder.AddInterceptors(interceptors);
        behavior.Initialise(builder);
        return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance), behavior);
    }

    private sealed class ConcurrencyRecorder(Action enter, Action exit) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            enter();
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            exit();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Runs code inside SaveChangesAsync, where it holds the permit, before and after it writes.
    /// </summary>
    private sealed class SaveChangesPause(Func<CancellationToken, Task> saving, Action saved) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await saving(cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            saved();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Makes each of two transactions that check for a row wait after its check, for up to a second, until the other
    /// has checked too. Unless the second cannot check before the first commits, both find the row missing.
    /// </summary>
    private sealed class CheckRendezvous
    {
        private readonly TaskCompletionSource _bothChecked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _checks;

        public Task CheckedAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _checks) == 2)
            {
                _bothChecked.SetResult();
            }

            return Task.WhenAny(_bothChecked.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
        }

        public void Checked(CancellationToken cancellationToken) => CheckedAsync(cancellationToken).Wait(cancellationToken);
    }

    private sealed class TransactionStartRecorder(Action starting, Action started) : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            starting();
            return result;
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            starting();
            return ValueTask.FromResult(result);
        }

        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            started();
            return result;
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            started();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingLogger : ILogger<SerializedWriteLockBehavior>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}

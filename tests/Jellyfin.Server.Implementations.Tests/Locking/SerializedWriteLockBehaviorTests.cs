using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        WriteInSynchronousTransaction();

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

    public void Dispose()
    {
        _behavior.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    // The synchronous code path is what this regression covers.
#pragma warning disable CA1849
    private void WriteInSynchronousTransaction()
    {
        using var context = CreateContext();
        using var transaction = context.Database.BeginTransaction();
        context.ActivityLogs.Add(new ActivityLog("sync", "type", Guid.NewGuid()));
        context.SaveChanges();
        transaction.Commit();
    }
#pragma warning restore CA1849

    private JellyfinDbContext CreateContext() => new(
        _options,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
        _behavior);

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
}

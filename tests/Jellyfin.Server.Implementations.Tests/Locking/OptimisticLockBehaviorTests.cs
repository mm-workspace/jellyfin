using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Locking;

/// <summary>
/// What the behaviour retries, driven through its save hooks, which never look at the context they are handed.
/// </summary>
public sealed class OptimisticLockBehaviorTests
{
    private static readonly IJellyfinDatabaseProvider _provider = new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance);

    [Fact]
    public void OnSaveChanges_DatabaseLocked_RetriesUntilItSucceeds()
    {
        var behavior = CreateBehavior();
        var attempts = 0;

        behavior.OnSaveChanges(null!, () =>
        {
            if (++attempts < 3)
            {
                throw DatabaseLocked();
            }
        });

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task OnSaveChangesAsync_DatabaseLocked_RetriesUntilItSucceeds()
    {
        var behavior = CreateBehavior();
        var attempts = 0;

        await behavior.OnSaveChangesAsync(null!, () =>
        {
            if (++attempts < 3)
            {
                throw DatabaseLocked();
            }

            return Task.CompletedTask;
        });

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void OnSaveChanges_UniqueViolation_FailsWithoutRetrying()
    {
        var behavior = CreateBehavior();
        var attempts = 0;

        Assert.Throws<DbUpdateException>(() => behavior.OnSaveChanges(null!, () =>
        {
            attempts++;
            throw UniqueViolation();
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task OnSaveChangesAsync_UniqueViolation_FailsWithoutRetrying()
    {
        var behavior = CreateBehavior();
        var attempts = 0;

        await Assert.ThrowsAsync<DbUpdateException>(() => behavior.OnSaveChangesAsync(null!, () =>
        {
            attempts++;
            throw UniqueViolation();
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void OnSaveChanges_FailureThatOnlyReadsLikeALockedDatabase_FailsWithoutRetrying()
    {
        var behavior = CreateBehavior();
        var attempts = 0;

        Assert.Throws<InvalidOperationException>(() => behavior.OnSaveChanges(null!, () =>
        {
            attempts++;
            throw new InvalidOperationException("database is locked");
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void OnSaveChanges_WithoutADatabaseProvider_FailsWithoutRetrying()
    {
        var behavior = new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance);
        var attempts = 0;

        Assert.Throws<SqliteException>(() => behavior.OnSaveChanges(null!, () =>
        {
            attempts++;
            throw DatabaseLocked();
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void OnSaveChanges_EveryRetryFails_ThrowsWhatTheDatabaseRaised()
    {
        // The waits are what the retries cost, so this one keeps them short enough to run them all.
        var behavior = new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance, _provider, [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]);
        var attempts = 0;

        Assert.Throws<SqliteException>(() => behavior.OnSaveChanges(null!, () =>
        {
            attempts++;
            throw DatabaseLocked();
        }));

        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task OnSaveChangesAsync_EveryRetryFails_ThrowsWhatTheDatabaseRaised()
    {
        var behavior = new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance, _provider, [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]);
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() => behavior.OnSaveChangesAsync(null!, () =>
        {
            attempts++;
            throw DatabaseLocked();
        }));

        Assert.Equal(4, attempts);
    }

    [Fact]
    public void Initialise_WithoutADatabaseProvider_WarnsThatNothingIsRetried()
    {
        var logger = new RecordingLogger();
        new OptimisticLockBehavior(logger).Initialise(new DbContextOptionsBuilder<JellyfinDbContext>());

        Assert.Contains(logger.Warnings, message => message.Contains("no write is retried", StringComparison.Ordinal));
    }

    [Fact]
    public void Initialise_WithADatabaseProvider_DoesNotWarn()
    {
        var logger = new RecordingLogger();
        new OptimisticLockBehavior(logger, _provider).Initialise(new DbContextOptionsBuilder<JellyfinDbContext>());

        Assert.Empty(logger.Warnings);
    }

    private static OptimisticLockBehavior CreateBehavior() => new(NullLogger<OptimisticLockBehavior>.Instance, _provider);

    // The codes SQLite raises for these, as SqliteDatabaseProviderClassifyTests shows against a real database.
    private static SqliteException DatabaseLocked() => new("SQLite Error 5: 'database is locked'.", 5, 5);

    private static DbUpdateException UniqueViolation() => new(
        "An error occurred while saving the entity changes.",
        new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: ItemValues.ItemValueId'.", 19, 1555));

    private sealed class RecordingLogger : ILogger<OptimisticLockBehavior>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

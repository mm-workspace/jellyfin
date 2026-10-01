using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

/// <summary>
/// Classification of the errors a PostgreSQL server really raises, rather than of exceptions the test built itself.
/// </summary>
[Trait("Provider", "PostgreSql")]
public sealed class PostgreSqlClassifyExceptionTests : IDisposable
{
    private const string LockFirstRow = "UPDATE \"ActivityLogs\" SET \"Overview\" = 'locked' WHERE \"Name\" = 'first'";
    private const string LockSecondRow = "UPDATE \"ActivityLogs\" SET \"Overview\" = 'locked' WHERE \"Name\" = 'second'";

    private readonly PostgreSqlTestDatabase? _database;

    public PostgreSqlClassifyExceptionTests()
    {
        if (TestDatabase.PostgreSqlConnectionString is { } connectionString)
        {
            _database = new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions());
        }
    }

    private PostgreSqlTestDatabase Database
    {
        get
        {
            Assert.SkipWhen(_database is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
            return _database;
        }
    }

    [Fact]
    public async Task ClassifyException_DuplicatePrimaryKey_IsUniqueViolation()
    {
        var itemValueId = Guid.NewGuid();
        await using (var context = Database.CreateDbContext())
        {
            context.ItemValues.Add(NewItemValue(itemValueId, "First"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var duplicating = Database.CreateDbContext();
        duplicating.ItemValues.Add(NewItemValue(itemValueId, "Second"));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => duplicating.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        Assert.Equal(DatabaseErrorKind.UniqueViolation, Database.Provider.ClassifyException(exception));
    }

    [Fact]
    public async Task ClassifyException_ConcurrentUpdateOfTheSameRow_IsTransient()
    {
        await SeedAsync();

        await using var first = Database.CreateDbContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.Current.CancellationToken);
        await first.Database.ExecuteSqlRawAsync(LockFirstRow, TestContext.Current.CancellationToken);

        // Waits for the transaction above, then finds the row changed under the snapshot it took while waiting.
        var second = Task.Run(
            async () =>
            {
                await using var context = Database.CreateDbContext();
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.Current.CancellationToken);
                await context.Database.ExecuteSqlRawAsync(LockFirstRow, TestContext.Current.CancellationToken);
                await transaction.CommitAsync(TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        await firstTransaction.CommitAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => second);

        Assert.Equal(PostgresErrorCodes.SerializationFailure, FindPostgresException(exception).SqlState);
        Assert.Equal(DatabaseErrorKind.Transient, Database.Provider.ClassifyException(exception));
    }

    [Fact]
    public async Task ClassifyException_TransactionsLockingTwoRowsInOppositeOrders_IsDeadlock()
    {
        await SeedAsync();

        var firstLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var locking = Task.Run(() => LockBothAsync(LockFirstRow, LockSecondRow, firstLocked, secondLocked.Task), TestContext.Current.CancellationToken);
        var lockingInReverse = Task.Run(() => LockBothAsync(LockSecondRow, LockFirstRow, secondLocked, firstLocked.Task), TestContext.Current.CancellationToken);

        var failures = (await Task.WhenAll(locking, lockingInReverse)).OfType<Exception>().ToList();

        var failure = Assert.Single(failures);
        Assert.Equal(PostgresErrorCodes.DeadlockDetected, FindPostgresException(failure).SqlState);
        Assert.Equal(DatabaseErrorKind.Deadlock, Database.Provider.ClassifyException(failure));
    }

    [Fact]
    public async Task ClassifyException_TerminatedConnection_IsTransient()
    {
        await using var context = Database.CreateDbContext();

        // Kept open, so the command below runs on the connection that is terminated rather than on a pooled one.
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var backend = await context.Database
            .SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(TestContext.Current.CancellationToken);

        await using (var terminator = new NpgsqlConnection(TestDatabase.PostgreSqlConnectionString))
        {
            await terminator.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@backend)", terminator);
            command.Parameters.AddWithValue("backend", backend);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => context.ActivityLogs.CountAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DatabaseErrorKind.Transient, Database.Provider.ClassifyException(exception));
    }

    [Fact]
    public async Task ClassifyException_ServerThatCannotBeReached_IsTransient()
    {
        IJellyfinDatabaseProvider provider = new PostgreSqlDatabaseProvider(null!, NullLogger<PostgreSqlDatabaseProvider>.Instance);

        // Port 1 is reserved, so nothing can be listening on it.
        var connectionString = new NpgsqlConnectionStringBuilder("Host=127.0.0.1;Username=jellyfin;Database=jellyfin")
        {
            Port = 1,
            Timeout = 2
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        var exception = await Assert.ThrowsAnyAsync<NpgsqlException>(() => connection.OpenAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DatabaseErrorKind.Transient, provider.ClassifyException(exception));
    }

    [Fact]
    public async Task ClassifyException_ErrorThatIsNeither_IsNone()
    {
        await using var context = Database.CreateDbContext();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync("SELECT * FROM \"NoSuchTable\"", TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.UndefinedTable, exception.SqlState);
        Assert.Equal(DatabaseErrorKind.None, Database.Provider.ClassifyException(exception));
    }

    public void Dispose()
    {
        _database?.Dispose();
    }

    private static ItemValue NewItemValue(Guid itemValueId, string value) => new()
    {
        ItemValueId = itemValueId,
        Type = ItemValueType.Genre,
        Value = value,
        CleanValue = value.ToLowerInvariant()
    };

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        throw new InvalidOperationException("The failure did not come from the server: " + exception, exception);
    }

    private async Task SeedAsync()
    {
        await using var context = Database.CreateDbContext();
        context.ActivityLogs.Add(new ActivityLog("first", "type", Guid.NewGuid()));
        context.ActivityLogs.Add(new ActivityLog("second", "type", Guid.NewGuid()));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Locks one row, waits until the other transaction has locked its own, and only then asks for that one.
    /// </summary>
    /// <returns>The failure, or <c>null</c> when this transaction is the one that got through.</returns>
    private async Task<Exception?> LockBothAsync(string lockFirst, string lockSecond, TaskCompletionSource locked, Task otherLocked)
    {
        try
        {
            await using var context = Database.CreateDbContext();
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync(lockFirst, TestContext.Current.CancellationToken);
            locked.SetResult();
            await otherLocked.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync(lockSecond, TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (PostgresException exception)
        {
            return exception;
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Implementations.Locking;

/// <summary>
/// A locking behavior that serializes writers in-process while leaving readers unsynchronized.
/// </summary>
/// <remarks>
/// <para>
/// SQLite permits one writer at a time; queueing them in-process lets each take the database lock
/// uncontended instead of racing for it and generating SQLITE_BUSY retries.
/// </para>
/// <para>
/// Reads are unsynchronized: in WAL mode they neither block nor are blocked by writers.
/// </para>
/// <para>
/// Held via <see cref="SemaphoreSlim"/> so it survives an <see langword="await"/>.
/// </para>
/// <para>
/// An explicit transaction holds the permit until it ends, and takes it before BEGIN. On SQLite the order
/// matters: Microsoft.Data.Sqlite issues BEGIN IMMEDIATE for every isolation level except ReadUncommitted,
/// so the database write lock is held from BEGIN to commit, and taking the permit after BEGIN would take the
/// two in the opposite order to SaveChanges, which holds the permit when it begins its own transaction.
/// </para>
/// <para>
/// PostgreSQL's BEGIN takes no lock, but a transaction there still takes the permit before BEGIN: Jellyfin
/// reads inside a transaction and then inserts what it found missing, relying on no other writer running in
/// between. A RepeatableRead, Serializable or Snapshot transaction, such as a backup's, is the exception: it
/// reads many tables from one snapshot, often for minutes, and holding the permit all that time would stall
/// every write in the server. It takes the permit on its first write, so one that only reads never holds it,
/// and it is serialized with other writers only from then on, so it must not be used to insert what it found
/// missing.
/// </para>
/// </remarks>
public sealed class SerializedWriteLockBehavior : IEntityFrameworkCoreLockingBehavior, IDisposable
{
    /// <summary>
    /// The provider name EF Core reports for PostgreSQL, the provider known to begin a transaction without a lock.
    /// </summary>
    private const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// How long to queue for the permit before proceeding without it and leaving busy_timeout to
    /// arbitrate. Reached whenever the holder is slow, not just on nested writes: a write that
    /// stalls for its full CommandTimeout holds the permit for that whole time, so every queued
    /// writer times out and then hits the database unsynchronized.
    /// </summary>
    private static readonly TimeSpan _defaultAcquireTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Set while this instance owns the permit. Propagates into the guarded call's EF internals so
    /// the nested interceptors skip re-acquiring.
    /// </summary>
    private readonly AsyncLocal<bool> _holdsWriteLock = new();

    private readonly TimeSpan _acquireTimeout;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Explicit transactions owning the permit, mapped to their connection. Keyed by transaction
    /// because its lifetime spans async flows; removal doubles as the release guard against the
    /// several end-of-transaction callbacks EF raises. Holds at most one entry.
    /// </summary>
    private readonly ConcurrentDictionary<DbTransaction, DbConnection> _lockedTransactions = new();

    private readonly ILogger<SerializedWriteLockBehavior> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SerializedWriteLockBehavior"/> class.
    /// </summary>
    /// <param name="logger">The application logger.</param>
    public SerializedWriteLockBehavior(ILogger<SerializedWriteLockBehavior> logger)
        : this(logger, _defaultAcquireTimeout)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SerializedWriteLockBehavior"/> class.
    /// </summary>
    /// <param name="logger">The application logger.</param>
    /// <param name="acquireTimeout">How long to queue for the permit before proceeding without it.</param>
    internal SerializedWriteLockBehavior(ILogger<SerializedWriteLockBehavior> logger, TimeSpan acquireTimeout)
    {
        _logger = logger;
        _acquireTimeout = acquireTimeout;
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder optionsBuilder)
    {
        _logger.LogInformation("The database locking mode has been set to: SerializedWrites.");
        optionsBuilder.AddInterceptors(new WriteSerializingCommandInterceptor(this));
        optionsBuilder.AddInterceptors(new WriteSerializingTransactionInterceptor(this));
        optionsBuilder.AddInterceptors(new WriteLockReleasingConnectionInterceptor(this));
    }

    /// <inheritdoc/>
    public void OnSaveChanges(JellyfinDbContext context, Action saveChanges)
    {
        if (HoldsWriteLock())
        {
            saveChanges();
            return;
        }

        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is not null)
        {
            LockTransaction(context, transaction, context.Database.GetDbConnection());
            saveChanges();
            return;
        }

        var acquired = Acquire();
        try
        {
            saveChanges();
        }
        finally
        {
            Release(acquired);
        }
    }

    /// <inheritdoc/>
    public async Task OnSaveChangesAsync(JellyfinDbContext context, Func<Task> saveChanges)
    {
        if (HoldsWriteLock())
        {
            await saveChanges().ConfigureAwait(false);
            return;
        }

        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is not null)
        {
            await LockTransactionAsync(context, transaction, context.Database.GetDbConnection(), CancellationToken.None).ConfigureAwait(false);
            await saveChanges().ConfigureAwait(false);
            return;
        }

        var acquired = await AcquireAsync(CancellationToken.None).ConfigureAwait(false);

        // Mark ownership in this frame: a value set inside AcquireAsync would not flow back out of it, and the
        // interceptors running inside saveChanges would then wait for the permit this call already holds.
        _holdsWriteLock.Value = acquired;
        try
        {
            await saveChanges().ConfigureAwait(false);
        }
        finally
        {
            Release(acquired);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }

    internal bool HoldsWriteLock() => _holdsWriteLock.Value;

    /// <summary>
    /// Whether an explicit transaction takes the permit before BEGIN rather than on its first write. Only a snapshot
    /// transaction on PostgreSQL waits for its first write; see the class remarks. Providers not known to begin
    /// without a lock are treated as SQLite.
    /// </summary>
    private static bool TakesPermitOnBegin(DbContext? context, IsolationLevel isolationLevel)
        => !string.Equals(context?.Database.ProviderName, PostgreSqlProviderName, StringComparison.Ordinal)
            || isolationLevel is not (IsolationLevel.RepeatableRead or IsolationLevel.Serializable or IsolationLevel.Snapshot);

    /// <summary>
    /// Makes an explicit transaction that is about to write hold the permit until it ends, if it does not already.
    /// Only a snapshot transaction on PostgreSQL, which takes the permit on its first write, waits for it here. Any
    /// other has already passed BEGIN with the permit or, after the wait for it timed out, without it; on SQLite it
    /// now holds the database write lock, and waiting again would take the two in the opposite order to SaveChanges.
    /// </summary>
    private void LockTransaction(DbContext? context, DbTransaction transaction, DbConnection connection)
    {
        if (NeedsTransactionLock(context, transaction) && AcquireForTransaction())
        {
            TrackTransaction(transaction, connection);
        }
    }

    /// <inheritdoc cref="LockTransaction"/>
    private async ValueTask LockTransactionAsync(DbContext? context, DbTransaction transaction, DbConnection connection, CancellationToken cancellationToken)
    {
        if (NeedsTransactionLock(context, transaction) && await AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            TrackTransaction(transaction, connection);
        }
    }

    private bool NeedsTransactionLock(DbContext? context, DbTransaction transaction)
        => !_lockedTransactions.ContainsKey(transaction) && !TakesPermitOnBegin(context, transaction.IsolationLevel);

    private bool Acquire()
    {
        var acquired = _writeLock.Wait(_acquireTimeout);
        if (acquired)
        {
            _holdsWriteLock.Value = true;
            return true;
        }

        LogAcquireTimeout();
        return false;
    }

    /// <summary>
    /// Waits for the permit. Callers that run nested database work mark ownership themselves after the await,
    /// because changes to an async local inside this method do not reach them.
    /// </summary>
    private async ValueTask<bool> AcquireAsync(CancellationToken cancellationToken)
    {
        var acquired = await _writeLock.WaitAsync(_acquireTimeout, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            LogAcquireTimeout();
        }

        return acquired;
    }

    /// <summary>
    /// Waits for the permit on behalf of an explicit transaction. Ownership is tracked by the transaction, not the
    /// async local, so it ends with the transaction instead of leaking into everything that later runs on this flow.
    /// </summary>
    private bool AcquireForTransaction()
    {
        var acquired = _writeLock.Wait(_acquireTimeout);
        if (!acquired)
        {
            LogAcquireTimeout();
        }

        return acquired;
    }

    private void LogAcquireTimeout()
    {
        _logger.LogWarning(
            "Timed out after {Timeout}s waiting for the in-process database write lock; proceeding without it. This means some write is holding the lock far too long, or that writes are nested across separate connections.",
            _acquireTimeout.TotalSeconds);
    }

    private void Release(bool acquired)
    {
        if (acquired)
        {
            _holdsWriteLock.Value = false;
            _writeLock.Release();
        }
    }

    private void TrackTransaction(DbTransaction transaction, DbConnection connection)
    {
        _lockedTransactions[transaction] = connection;
    }

    private void ReleaseTransaction(DbTransaction transaction)
    {
        if (_lockedTransactions.TryRemove(transaction, out _))
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Releases a permit still held by a transaction on a closing connection. EF disposes the
    /// underlying transaction without a commit, rollback or failure callback, leaving this the only
    /// release point for a transaction abandoned by an exception.
    /// </summary>
    private void ReleaseTransactionsOn(DbConnection connection)
    {
        foreach (var (transaction, owner) in _lockedTransactions)
        {
            if (ReferenceEquals(owner, connection))
            {
                ReleaseTransaction(transaction);
            }
        }
    }

    /// <summary>
    /// Serializes writes issued outside <c>SaveChanges</c>: ExecuteDelete, ExecuteUpdate, raw SQL and
    /// migrations, all of which execute as non-queries. Outside an explicit transaction the permit is held
    /// for the command; inside one, for the rest of the transaction. Reads pass through.
    /// </summary>
    private sealed class WriteSerializingCommandInterceptor : DbCommandInterceptor
    {
        private readonly SerializedWriteLockBehavior _owner;

        public WriteSerializingCommandInterceptor(SerializedWriteLockBehavior owner)
        {
            _owner = owner;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (!NeedsLock(eventData))
            {
                return base.NonQueryExecuting(command, eventData, result);
            }

            if (command.Transaction is not null)
            {
                _owner.LockTransaction(eventData.Context, command.Transaction, eventData.Connection);
                return base.NonQueryExecuting(command, eventData, result);
            }

            var acquired = _owner.Acquire();
            try
            {
                return InterceptionResult<int>.SuppressWithResult(command.ExecuteNonQuery());
            }
            finally
            {
                _owner.Release(acquired);
            }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!NeedsLock(eventData))
            {
                return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
            }

            if (command.Transaction is not null)
            {
                await _owner.LockTransactionAsync(eventData.Context, command.Transaction, eventData.Connection, cancellationToken).ConfigureAwait(false);
                return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
            }

            var acquired = await _owner.AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return InterceptionResult<int>.SuppressWithResult(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                _owner.Release(acquired);
            }
        }

        private bool NeedsLock(CommandEventData eventData)
        {
            if (!IsWrite(eventData.CommandSource))
            {
                return false;
            }

            // The semaphore is not reentrant; taking it again under an owning operation deadlocks.
            return !_owner.HoldsWriteLock();
        }

        private static bool IsWrite(CommandSource source) => source switch
        {
            CommandSource.SaveChanges => true,
            CommandSource.Migrations => true,
            CommandSource.ExecuteSqlRaw => true,
            CommandSource.ExecuteUpdate => true,
            CommandSource.ExecuteDelete => true,
            _ => false,
        };
    }

    /// <summary>
    /// Holds the permit for the lifetime of an explicit transaction that takes it on BEGIN. Acquires on
    /// <c>TransactionStarting</c>, before BEGIN, and begins the transaction itself: a BEGIN that fails
    /// leaves no transaction for a commit, rollback or connection close to release on.
    /// </summary>
    private sealed class WriteSerializingTransactionInterceptor : DbTransactionInterceptor
    {
        private readonly SerializedWriteLockBehavior _owner;

        public WriteSerializingTransactionInterceptor(SerializedWriteLockBehavior owner)
        {
            _owner = owner;
        }

        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            if (!NeedsLock(eventData, result) || !_owner.AcquireForTransaction())
            {
                return base.TransactionStarting(connection, eventData, result);
            }

            try
            {
                var transaction = connection.BeginTransaction(eventData.IsolationLevel);
                _owner.TrackTransaction(transaction, connection);
                return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
            }
            catch
            {
                _owner._writeLock.Release();
                throw;
            }
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (!NeedsLock(eventData, result) || !await _owner.AcquireAsync(cancellationToken).ConfigureAwait(false))
            {
                return await base.TransactionStartingAsync(connection, eventData, result, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var transaction = await connection.BeginTransactionAsync(eventData.IsolationLevel, cancellationToken).ConfigureAwait(false);
                _owner.TrackTransaction(transaction, connection);
                return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
            }
            catch
            {
                _owner._writeLock.Release();
                throw;
            }
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            _owner.ReleaseTransaction(transaction);
            base.TransactionCommitted(transaction, eventData);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            _owner.ReleaseTransaction(transaction);
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        {
            _owner.ReleaseTransaction(transaction);
            base.TransactionRolledBack(transaction, eventData);
        }

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            _owner.ReleaseTransaction(transaction);
            return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        {
            _owner.ReleaseTransaction(transaction);
            base.TransactionFailed(transaction, eventData);
        }

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            _owner.ReleaseTransaction(transaction);
            return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
        }

        /// <summary>
        /// A transaction begun under an owning operation, such as the one SaveChanges begins, runs under that
        /// operation's permit. A transaction another interceptor has already begun is past BEGIN, and a snapshot
        /// transaction on PostgreSQL waits for its first write.
        /// </summary>
        private bool NeedsLock(TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
            => !result.HasResult && !_owner.HoldsWriteLock() && TakesPermitOnBegin(eventData.Context, eventData.IsolationLevel);
    }

    /// <summary>
    /// Backstop release for transactions abandoned without a commit or rollback callback.
    /// </summary>
    private sealed class WriteLockReleasingConnectionInterceptor : DbConnectionInterceptor
    {
        private readonly SerializedWriteLockBehavior _owner;

        public WriteLockReleasingConnectionInterceptor(SerializedWriteLockBehavior owner)
        {
            _owner = owner;
        }

        public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
        {
            _owner.ReleaseTransactionsOn(connection);
            base.ConnectionClosed(connection, eventData);
        }

        public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
        {
            _owner.ReleaseTransactionsOn(connection);
            return base.ConnectionClosedAsync(connection, eventData);
        }
    }
}

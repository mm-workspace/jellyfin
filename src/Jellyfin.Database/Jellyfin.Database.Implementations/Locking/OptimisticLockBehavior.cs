#pragma warning disable CA1873

using System;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly;

namespace Jellyfin.Database.Implementations.Locking;

/// <summary>
/// Defines a locking mechanism that will retry any write operation for a few times.
/// </summary>
public class OptimisticLockBehavior : IEntityFrameworkCoreLockingBehavior
{
    private static readonly TimeSpan[] _defaultSleepDurations = [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromSeconds(3)
    ];

    private readonly Policy _writePolicy;
    private readonly AsyncPolicy _writeAsyncPolicy;
    private readonly ILogger<OptimisticLockBehavior> _logger;
    private readonly IJellyfinDatabaseProvider? _databaseProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimisticLockBehavior"/> class.
    /// </summary>
    /// <param name="logger">The application logger.</param>
    public OptimisticLockBehavior(ILogger<OptimisticLockBehavior> logger)
        : this(logger, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimisticLockBehavior"/> class.
    /// </summary>
    /// <param name="logger">The application logger.</param>
    /// <param name="databaseProvider">
    /// The database provider, whose classification of a failure decides what is retried, or <see langword="null"/>
    /// to retry nothing because no provider can tell the retryable failures apart from the rest.
    /// </param>
    public OptimisticLockBehavior(ILogger<OptimisticLockBehavior> logger, IJellyfinDatabaseProvider? databaseProvider)
        : this(logger, databaseProvider, _defaultSleepDurations)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimisticLockBehavior"/> class.
    /// </summary>
    /// <param name="logger">The application logger.</param>
    /// <param name="databaseProvider">The database provider, whose classification of a failure decides what is retried.</param>
    /// <param name="sleepDurations">How long to wait before each retry.</param>
    internal OptimisticLockBehavior(ILogger<OptimisticLockBehavior> logger, IJellyfinDatabaseProvider? databaseProvider, TimeSpan[] sleepDurations)
    {
        _databaseProvider = databaseProvider;

        // Polly counts the retries from one.
        Func<int, Context, TimeSpan> backoffProvider = (retryNo, context) =>
        {
            var backoff = sleepDurations[retryNo - 1];

            // Spread the waiting writers out, as far as half the wait leaves room for.
            var jitter = (int)(backoff.TotalMilliseconds * .5);
            return backoff + (jitter > 0 ? TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, jitter)) : TimeSpan.Zero);
        };

        _logger = logger;
        _writePolicy = Policy
            .HandleInner<Exception>(IsWorthRetrying)
            .WaitAndRetry(sleepDurations.Length, backoffProvider, RetryHandle);
        _writeAsyncPolicy = Policy
            .HandleInner<Exception>(IsWorthRetrying)
            .WaitAndRetryAsync(sleepDurations.Length, backoffProvider, RetryHandle);

        // Only what the database says it held up, so a write that conflicts with a stored row fails at once
        // instead of being repeated for seconds to fail the same way.
        bool IsWorthRetrying(Exception exception)
            => databaseProvider?.ClassifyException(exception) == DatabaseErrorKind.Transient;

        void RetryHandle(Exception exception, TimeSpan timespan, int retryNo, Context context)
        {
            if (retryNo < sleepDurations.Length)
            {
                _logger.LogWarning("Operation failed retry {RetryNo}", retryNo);
            }
            else
            {
                _logger.LogError(exception, "Operation failed retry {RetryNo}", retryNo);
            }
        }
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder optionsBuilder)
    {
        _logger.LogInformation("The database locking mode has been set to: Optimistic.");
        if (_databaseProvider is null)
        {
            // Without a provider nothing tells a failure worth retrying from the rest, so the mode retries nothing.
            _logger.LogWarning("The database provider cannot classify its failures, so no write is retried. Use another locking mode.");
        }

        optionsBuilder.AddInterceptors(new RetryInterceptor(_writeAsyncPolicy, _writePolicy));
        optionsBuilder.AddInterceptors(new TransactionLockingInterceptor(_writeAsyncPolicy, _writePolicy));
    }

    /// <inheritdoc/>
    public void OnSaveChanges(JellyfinDbContext context, Action saveChanges)
    {
        _writePolicy.Execute(saveChanges);
    }

    /// <inheritdoc/>
    public async Task OnSaveChangesAsync(JellyfinDbContext context, Func<Task> saveChanges)
    {
        await _writeAsyncPolicy.ExecuteAsync(saveChanges).ConfigureAwait(false);
    }

    private sealed class TransactionLockingInterceptor : DbTransactionInterceptor
    {
        private readonly AsyncPolicy _asyncRetryPolicy;
        private readonly Policy _retryPolicy;

        public TransactionLockingInterceptor(AsyncPolicy asyncRetryPolicy, Policy retryPolicy)
        {
            _asyncRetryPolicy = asyncRetryPolicy;
            _retryPolicy = retryPolicy;
        }

        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            return InterceptionResult<DbTransaction>.SuppressWithResult(_retryPolicy.Execute(() => connection.BeginTransaction(eventData.IsolationLevel)));
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<DbTransaction>.SuppressWithResult(await _asyncRetryPolicy.ExecuteAsync(async () => await connection.BeginTransactionAsync(eventData.IsolationLevel, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }
    }

    private sealed class RetryInterceptor : DbCommandInterceptor
    {
        private readonly AsyncPolicy _asyncRetryPolicy;
        private readonly Policy _retryPolicy;

        public RetryInterceptor(AsyncPolicy asyncRetryPolicy, Policy retryPolicy)
        {
            _asyncRetryPolicy = asyncRetryPolicy;
            _retryPolicy = retryPolicy;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            return InterceptionResult<int>.SuppressWithResult(_retryPolicy.Execute(command.ExecuteNonQuery));
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<int>.SuppressWithResult(await _asyncRetryPolicy.ExecuteAsync(async () => await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            return InterceptionResult<object>.SuppressWithResult(_retryPolicy.Execute(() => command.ExecuteScalar()!));
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<object>.SuppressWithResult((await _asyncRetryPolicy.ExecuteAsync(async () => await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)!).ConfigureAwait(false))!);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(_retryPolicy.Execute(command.ExecuteReader));
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(await _asyncRetryPolicy.ExecuteAsync(async () => await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }
    }
}

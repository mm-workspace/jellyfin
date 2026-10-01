using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Jellyfin.Server.HealthChecks;

/// <summary>
/// Implementation of the <see cref="DbContextHealthCheck{TContext}"/> for a <see cref="IDbContextFactory{TContext}"/>.
/// </summary>
/// <typeparam name="TContext">The type of database context.</typeparam>
public class DbContextFactoryHealthCheck<TContext> : IHealthCheck
    where TContext : DbContext
{
    private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IDbContextFactory<TContext> _dbContextFactory;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="DbContextFactoryHealthCheck{TContext}"/> class.
    /// </summary>
    /// <param name="contextFactory">Instance of the <see cref="IDbContextFactory{TContext}"/> interface.</param>
    public DbContextFactoryHealthCheck(IDbContextFactory<TContext> contextFactory)
        : this(contextFactory, _defaultTimeout)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbContextFactoryHealthCheck{TContext}"/> class.
    /// </summary>
    /// <param name="contextFactory">Instance of the <see cref="IDbContextFactory{TContext}"/> interface.</param>
    /// <param name="timeout">How long the database may take to answer before it is reported unhealthy.</param>
    internal DbContextFactoryHealthCheck(IDbContextFactory<TContext> contextFactory, TimeSpan timeout)
    {
        _dbContextFactory = contextFactory;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var canConnect = CanConnectAsync(timeout.Token);
        try
        {
            // Opening a connection does not always stop when cancelled, e.g. while waiting for a stalled server to answer.
            return await canConnect.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _ = canConnect.ContinueWith(static t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return HealthCheckResult.Unhealthy("The database did not answer in time.");
        }
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

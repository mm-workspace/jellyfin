using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Runs the <see cref="PostgreSqlStartupChecks"/> on the first connection to the configured database.
/// </summary>
internal sealed class PostgreSqlStartupCheckInterceptor : DbConnectionInterceptor
{
    private readonly PostgreSqlStartupChecks _checks;
    private readonly string? _database;
    private int _passed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlStartupCheckInterceptor"/> class.
    /// </summary>
    /// <param name="checks">The checks.</param>
    /// <param name="connectionString">The connection string of the configured database.</param>
    public PostgreSqlStartupCheckInterceptor(PostgreSqlStartupChecks checks, string connectionString)
    {
        _checks = checks;
        using var connection = new NpgsqlConnection(connectionString);
        _database = connection.Database;
    }

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (ShouldRun(connection))
        {
            // This happens once per process, so blocking the first synchronous open is acceptable.
            RunAsync(connection, eventData, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (ShouldRun(connection))
        {
            await RunAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ShouldRun(DbConnection connection)
    {
        // Creating the database connects to an administrative database first, which is not the one to check.
        return Volatile.Read(ref _passed) == 0 && string.Equals(connection.Database, _database, StringComparison.Ordinal);
    }

    private async Task RunAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken)
    {
        // Checks that fail run again on the next connection, so fixing the database does not need a restart.
        await _checks.RunAsync((NpgsqlConnection)connection, eventData.Context, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _passed, 1);
    }
}

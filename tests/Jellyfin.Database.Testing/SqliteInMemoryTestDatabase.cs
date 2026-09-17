using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Database.Testing;

/// <summary>
/// An in-memory SQLite database. The open connection owns the database, so it lives as long as this instance.
/// </summary>
public sealed class SqliteInMemoryTestDatabase : ITestDatabase
{
    private readonly TestDatabaseOptions _options;
    private SqliteConnection _connection;
    private DbContextOptions<JellyfinDbContext> _dbOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteInMemoryTestDatabase"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    public SqliteInMemoryTestDatabase(TestDatabaseOptions options)
    {
        _options = options;
        Provider = new SqliteDatabaseProvider(options.ApplicationPaths!, NullLogger<SqliteDatabaseProvider>.Instance);
        (_connection, _dbOptions) = Open();
    }

    /// <inheritdoc />
    public string ProviderKey => "Jellyfin-SQLite";

    /// <inheritdoc />
    public IJellyfinDatabaseProvider Provider { get; }

    /// <inheritdoc />
    public DbContextOptions<JellyfinDbContext> Options => _dbOptions;

    /// <inheritdoc />
    public JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        Provider,
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    /// <inheritdoc />
    public IDbContextFactory<JellyfinDbContext> CreateDbContextFactory() => new TestDbContextFactory(CreateDbContext);

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _connection.Dispose();
        (_connection, _dbOptions) = Open();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _connection.DisposeAsync();
    }

    private (SqliteConnection Connection, DbContextOptions<JellyfinDbContext> Options) Open()
    {
        // Foreign keys are enforced by default, as they are in a running server.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var builder = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(connection);
        if (_options.Interceptors.Count > 0)
        {
            builder.AddInterceptors(_options.Interceptors);
        }

        _options.ConfigureOptions?.Invoke(builder);
        var dbOptions = builder.Options;

        _dbOptions = dbOptions;
        using (var context = CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        return (connection, dbOptions);
    }
}

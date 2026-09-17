using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jellyfin.Database.Testing;

/// <summary>
/// A PostgreSQL database created for one test class on the server configured for tests, and dropped again afterwards.
/// </summary>
public sealed class PostgreSqlTestDatabase : ITestDatabase
{
    private static readonly string _runId = Guid.NewGuid().ToString("N")[..8];
    private static int _counter;

    private readonly TestDatabaseOptions _options;
    private readonly string _serverConnectionString;
    private readonly string _databaseName;
    private readonly string _connectionString;
    private DbContextOptions<JellyfinDbContext> _dbOptions;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlTestDatabase"/> class.
    /// </summary>
    /// <param name="serverConnectionString">A connection string to the server. The role must be allowed to create databases.</param>
    /// <param name="options">The options.</param>
    public PostgreSqlTestDatabase(string serverConnectionString, TestDatabaseOptions options)
    {
        _options = options;
        _serverConnectionString = serverConnectionString;
        _databaseName = string.Create(CultureInfo.InvariantCulture, $"jf_t_{_runId}_{Interlocked.Increment(ref _counter)}");
        _connectionString = new NpgsqlConnectionStringBuilder(serverConnectionString)
        {
            Database = _databaseName,
            MaxPoolSize = 5
        }.ConnectionString;

        Provider = new PostgreSqlDatabaseProvider(options.ApplicationPaths!, NullLogger<PostgreSqlDatabaseProvider>.Instance);
        _dbOptions = BuildOptions();
        CreateDatabase();
    }

    /// <summary>
    /// Gets the name of the database.
    /// </summary>
    public string DatabaseName => _databaseName;

    /// <summary>
    /// Gets the connection string of the database.
    /// </summary>
    public string ConnectionString => _connectionString;

    /// <inheritdoc />
    public string ProviderKey => "Jellyfin-PostgreSQL";

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
        DropDatabase();
        CreateDatabase();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DropDatabase();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private DbContextOptions<JellyfinDbContext> BuildOptions()
    {
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        Provider.Initialise(builder, new DatabaseConfigurationOptions
        {
            DatabaseType = ProviderKey,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = _connectionString
            }
        });

        if (_options.Interceptors.Count > 0)
        {
            builder.AddInterceptors(_options.Interceptors);
        }

        _options.ConfigureOptions?.Invoke(builder);
        return builder.Options;
    }

    private void CreateDatabase()
    {
        ExecuteOnServer($"CREATE DATABASE \"{_databaseName}\" TEMPLATE template0 ENCODING 'UTF8'");
        using var context = CreateDbContext();
        context.Database.Migrate();
    }

    private void DropDatabase()
    {
        using (var connection = new NpgsqlConnection(_connectionString))
        {
            NpgsqlConnection.ClearPool(connection);
        }

        ExecuteOnServer($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only statements built from the generated database name are executed.")]
    private void ExecuteOnServer(string sql)
    {
        using var connection = new NpgsqlConnection(_serverConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

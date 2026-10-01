using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Testing;
using Jellyfin.Server.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Tests.HealthChecks;

public sealed class DbContextFactoryHealthCheckTests : IDisposable
{
    private readonly TcpListener _silentServer = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public DbContextFactoryHealthCheckTests()
    {
        _silentServer.Start();
    }

    [Fact]
    public async Task CheckHealthAsync_DatabaseDoesNotAnswer_ReportsUnhealthyAfterTimeout()
    {
        // Accept connections and never answer, like a stalled server.
        var accepted = AcceptWithoutAnsweringAsync();
        var port = ((IPEndPoint)_silentServer.LocalEndpoint).Port;
        var healthCheck = new DbContextFactoryHealthCheck<JellyfinDbContext>(
            new PostgreSqlContextFactory($"Host=127.0.0.1;Port={port};Database=jellyfin;SSL Mode=Disable;Timeout=60;Pooling=false"),
            TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6));
        await _stop.CancelAsync();
        await accepted;
    }

    [Fact]
    public async Task CheckHealthAsync_DatabaseAvailable_ReportsHealthy()
    {
        using var database = new SqliteInMemoryTestDatabase(new TestDatabaseOptions());
        var healthCheck = new DbContextFactoryHealthCheck<JellyfinDbContext>(database.CreateDbContextFactory());

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_CallerCancels_Throws()
    {
        using var database = new SqliteInMemoryTestDatabase(new TestDatabaseOptions());
        var healthCheck = new DbContextFactoryHealthCheck<JellyfinDbContext>(database.CreateDbContextFactory());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => healthCheck.CheckHealthAsync(new HealthCheckContext(), cancellation.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _silentServer.Stop();
        _silentServer.Dispose();
    }

    private async Task AcceptWithoutAnsweringAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _silentServer.AcceptTcpClientAsync(_stop.Token);
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The test is done.
        }
    }

    private sealed class PostgreSqlContextFactory : IDbContextFactory<JellyfinDbContext>
    {
        private readonly PostgreSqlDatabaseProvider _provider = new(null!, NullLogger<PostgreSqlDatabaseProvider>.Instance);
        private readonly DbContextOptions<JellyfinDbContext> _options;

        public PostgreSqlContextFactory(string connectionString)
        {
            var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
            _provider.Initialise(builder, new DatabaseConfigurationOptions
            {
                DatabaseType = "Jellyfin-PostgreSQL",
                CustomProviderOptions = new CustomDatabaseOptions { PluginName = string.Empty, PluginAssembly = string.Empty, ConnectionString = connectionString }
            });
            _options = builder.Options;
        }

        public JellyfinDbContext CreateDbContext()
            => new(_options, NullLogger<JellyfinDbContext>.Instance, _provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}

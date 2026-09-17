using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Server.Implementations.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

public class ProviderRegistrationTests
{
    [Theory]
    [InlineData("Jellyfin-PostgreSQL")]
    [InlineData("jellyfin-postgresql")]
    public void AddJellyfinDbContext_PostgreSqlKey_ResolvesPostgreSqlProvider(string databaseType)
    {
        using var serviceProvider = BuildServices(new DatabaseConfigurationOptions
        {
            DatabaseType = databaseType,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = "Host=127.0.0.1;Database=jellyfin"
            }
        });

        Assert.IsType<PostgreSqlDatabaseProvider>(serviceProvider.GetRequiredService<IJellyfinDatabaseProvider>());

        using var context = serviceProvider.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContext();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
    }

    [Fact]
    public void AddJellyfinDbContext_UnknownKey_ListsSupportedProviders()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BuildServices(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-Unknown" }));

        Assert.Contains("Jellyfin-SQLite", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Jellyfin-PostgreSQL", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseLockingBehaviorTypes.NoLock)]
    [InlineData(DatabaseLockingBehaviorTypes.SerializedWrites)]
    public void AddJellyfinDbContext_PostgreSqlLockingBehavior_SerializesWrites(DatabaseLockingBehaviorTypes lockingBehavior)
    {
        var logs = new RecordingLoggerProvider();
        using var serviceProvider = BuildServices(PostgreSqlConfiguration(lockingBehavior), logs);

        Assert.IsType<SerializedWriteLockBehavior>(serviceProvider.GetRequiredService<IEntityFrameworkCoreLockingBehavior>());
        using var context = serviceProvider.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContext();

        var effectiveLog = logs.Messages.Where(m => m.Contains("effective database locking behavior", StringComparison.Ordinal)).ToList();
        if (lockingBehavior == DatabaseLockingBehaviorTypes.NoLock)
        {
            Assert.Contains(effectiveLog, m => m.Contains("SerializedWrites", StringComparison.Ordinal) && m.Contains("NoLock", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(effectiveLog);
        }
    }

    [Theory]
    [InlineData(DatabaseLockingBehaviorTypes.Optimistic)]
    [InlineData(DatabaseLockingBehaviorTypes.Pessimistic)]
    public void AddJellyfinDbContext_PostgreSqlUnsupportedLockingBehavior_Throws(DatabaseLockingBehaviorTypes lockingBehavior)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BuildServices(PostgreSqlConfiguration(lockingBehavior)));

        Assert.Contains(lockingBehavior.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("SerializedWrites", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseLockingBehaviorTypes.Optimistic)]
    [InlineData(DatabaseLockingBehaviorTypes.Pessimistic)]
    public void Initialise_PostgreSqlUnsupportedLockingBehavior_Throws(DatabaseLockingBehaviorTypes lockingBehavior)
    {
        var provider = new PostgreSqlDatabaseProvider(null!, NullLogger<PostgreSqlDatabaseProvider>.Instance);

        Assert.Throws<InvalidOperationException>(() => provider.Initialise(new DbContextOptionsBuilder<JellyfinDbContext>(), PostgreSqlConfiguration(lockingBehavior)));
    }

    [Theory]
    [InlineData(DatabaseLockingBehaviorTypes.NoLock, typeof(NoLockBehavior))]
    [InlineData(DatabaseLockingBehaviorTypes.Optimistic, typeof(OptimisticLockBehavior))]
    [InlineData(DatabaseLockingBehaviorTypes.Pessimistic, typeof(PessimisticLockBehavior))]
    [InlineData(DatabaseLockingBehaviorTypes.SerializedWrites, typeof(SerializedWriteLockBehavior))]
    public void AddJellyfinDbContext_SqliteLockingBehavior_IsUsedAsConfigured(DatabaseLockingBehaviorTypes lockingBehavior, Type expected)
    {
        using var serviceProvider = BuildServices(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite", LockingBehavior = lockingBehavior });

        Assert.IsType(expected, serviceProvider.GetRequiredService<IEntityFrameworkCoreLockingBehavior>());
    }

    [Theory]
    [InlineData("Jellyfin.Plugin.Pgsql", "Jellyfin.Plugin.Pgsql")]
    [InlineData("PostgreSQL", "Jellyfin.Plugin.Pgsql.dll")]
    [InlineData("Pgsql", null)]
    [InlineData(null, "Jellyfin.Plugin.Pgsql")]
    public void AddJellyfinDbContext_PostgreSqlPlugin_RefusesWithGuidance(string? pluginName, string? pluginAssembly)
    {
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = pluginName!,
                PluginAssembly = pluginAssembly!,
                ConnectionString = "Host=db;Password=secret"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() => BuildServices(configuration));

        Assert.Contains("PostgreSQL plugin", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Jellyfin-PostgreSQL", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Jellyfin-PgSql")]
    [InlineData("jellyfin-pgsql")]
    public void AddJellyfinDbContext_PostgreSqlPluginKey_RefusesWithGuidance(string databaseType)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BuildServices(PostgreSqlConfiguration(DatabaseLockingBehaviorTypes.NoLock, databaseType)));

        Assert.Contains("PostgreSQL plugin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddJellyfinDbContext_OtherPlugin_IsLoadedFromThePluginsFolder()
    {
        var pluginsPath = Path.Combine(Path.GetTempPath(), "jellyfin-test-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginsPath);
        try
        {
            var configuration = new DatabaseConfigurationOptions
            {
                DatabaseType = "PLUGIN_PROVIDER",
                CustomProviderOptions = new CustomDatabaseOptions { PluginName = "MySql", PluginAssembly = "Jellyfin.Plugin.MySql", ConnectionString = string.Empty }
            };

            var exception = Assert.Throws<InvalidOperationException>(() => BuildServices(configuration, pluginsPath: pluginsPath));

            Assert.Contains("could not been found", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(pluginsPath, true);
        }
    }

    private static DatabaseConfigurationOptions PostgreSqlConfiguration(DatabaseLockingBehaviorTypes lockingBehavior, string databaseType = "Jellyfin-PostgreSQL") => new()
    {
        DatabaseType = databaseType,
        LockingBehavior = lockingBehavior,
        CustomProviderOptions = new CustomDatabaseOptions
        {
            PluginName = string.Empty,
            PluginAssembly = string.Empty,
            ConnectionString = "Host=127.0.0.1;Database=jellyfin;Password=secret"
        }
    };

    private static ServiceProvider BuildServices(DatabaseConfigurationOptions databaseConfiguration, ILoggerProvider? loggerProvider = null, string? pluginsPath = null)
    {
        var applicationPaths = new Mock<IServerApplicationPaths>();
        applicationPaths.SetupGet(p => p.PluginsPath).Returns(pluginsPath ?? Path.GetTempPath());
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.GetConfiguration("database")).Returns(databaseConfiguration);
        configurationManager.SetupGet(c => c.ApplicationPaths).Returns(applicationPaths.Object);

        return new ServiceCollection()
            .AddLogging(builder =>
            {
                if (loggerProvider is not null)
                {
                    builder.AddProvider(loggerProvider);
                }
            })
            .AddSingleton<IApplicationPaths>(applicationPaths.Object)
            .AddJellyfinDbContext(configurationManager.Object, new ConfigurationBuilder().Build())
            .BuildServiceProvider();
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}

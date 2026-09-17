using System;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Server.Implementations.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private static ServiceProvider BuildServices(DatabaseConfigurationOptions databaseConfiguration)
    {
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.GetConfiguration("database")).Returns(databaseConfiguration);

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(new Mock<IApplicationPaths>().Object)
            .AddJellyfinDbContext(configurationManager.Object, new ConfigurationBuilder().Build())
            .BuildServiceProvider();
    }
}

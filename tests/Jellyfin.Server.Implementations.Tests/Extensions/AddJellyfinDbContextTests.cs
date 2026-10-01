using System;
using System.Collections.Generic;
using System.IO;
using Emby.Server.Implementations;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.DatabaseConfiguration;
using Jellyfin.Server.Implementations.Extensions;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ServerConfigurationManager = Emby.Server.Implementations.Configuration.ServerConfigurationManager;

namespace Jellyfin.Server.Implementations.Tests.Extensions;

public sealed class AddJellyfinDbContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyfin-database-config-tests", Guid.NewGuid().ToString("N"));
    private readonly ServerApplicationPaths _paths;

    public AddJellyfinDbContextTests()
    {
        _paths = new ServerApplicationPaths(
            Path.Combine(_root, "data"),
            Path.Combine(_root, "log"),
            Path.Combine(_root, "config"),
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "web"));
        Directory.CreateDirectory(_paths.ConfigurationDirectoryPath);
        Directory.CreateDirectory(_paths.DataPath);
    }

    private string DatabaseConfigurationFile => Path.Combine(_paths.ConfigurationDirectoryPath, "database.xml");

    [Fact]
    public void AddJellyfinDbContext_NoConfigurationFile_UsesAndSavesSqlite()
    {
        using var services = BuildServices();

        Assert.IsType<SqliteDatabaseProvider>(services.GetRequiredService<IJellyfinDatabaseProvider>());
        Assert.Contains("Jellyfin-SQLite", File.ReadAllText(DatabaseConfigurationFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DatabaseConfigurationOptions>\n  <DatabaseType>Jellyfin-PostgreSQL</DatabaseType>\n")]
    [InlineData("not xml")]
    [InlineData("")]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DatabaseConfigurationOptions xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n  <LockingBehavior>NoLock</LockingBehavior>\n</DatabaseConfigurationOptions>")]
    public void AddJellyfinDbContext_UnreadableConfigurationFile_ThrowsAndKeepsTheFile(string content)
    {
        File.WriteAllText(DatabaseConfigurationFile, content);
        var before = File.ReadAllBytes(DatabaseConfigurationFile);

        var exception = Assert.Throws<InvalidOperationException>(() => BuildServices());

        Assert.Contains(DatabaseConfigurationFile, exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(DatabaseConfigurationFile));
    }

    [Fact]
    public void AddJellyfinDbContext_EmptyDatabaseType_ListsSupportedProviders()
    {
        WriteConfiguration(string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(() => BuildServices());

        Assert.Contains("Jellyfin-SQLite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddJellyfinDbContext_LowercaseDatabaseType_IsAccepted()
    {
        WriteConfiguration("jellyfin-sqlite");

        using var services = BuildServices();

        Assert.IsType<SqliteDatabaseProvider>(services.GetRequiredService<IJellyfinDatabaseProvider>());
    }

    [Fact]
    public void AddJellyfinDbContext_MigrationProviderArgument_IsUsedWithoutSaving()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["migration-provider"] = "Jellyfin-SQLite" });

        Assert.IsType<SqliteDatabaseProvider>(services.GetRequiredService<IJellyfinDatabaseProvider>());
        Assert.False(File.Exists(DatabaseConfigurationFile));
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private void WriteConfiguration(string databaseType)
    {
        File.WriteAllText(
            DatabaseConfigurationFile,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DatabaseConfigurationOptions xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n  <DatabaseType>{databaseType}</DatabaseType>\n  <LockingBehavior>NoLock</LockingBehavior>\n</DatabaseConfigurationOptions>");
    }

    private ServiceProvider BuildServices(Dictionary<string, string?>? startupConfiguration = null)
    {
        var configurationManager = new ServerConfigurationManager(_paths, NullLoggerFactory.Instance, new MyXmlSerializer());
        configurationManager.AddParts([new DatabaseConfigurationFactory()]);

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IApplicationPaths>(_paths)
            .AddJellyfinDbContext(configurationManager, new ConfigurationBuilder().AddInMemoryCollection(startupConfiguration ?? []).Build())
            .BuildServiceProvider();
    }
}

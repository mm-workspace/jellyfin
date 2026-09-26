using System;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Base fixture for tests that run against a database: one test database per test class, plus the wiring the
/// repositories under test need. The database provider is selected by <see cref="TestDatabase"/>. Derived classes
/// seed in their own constructor.
/// </summary>
public abstract class DbTestFixture : IDisposable
{
    protected DbTestFixture(params IInterceptor[] interceptors)
    {
        ApplicationPaths = new Mock<IApplicationPaths>().Object;
        Database = TestDatabase.Create(new TestDatabaseOptions
        {
            Interceptors = interceptors,
            ApplicationPaths = ApplicationPaths
        });
    }

    protected IApplicationPaths ApplicationPaths { get; }

    protected ITestDatabase Database { get; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected JellyfinDbContext CreateDbContext() => Database.CreateDbContext();

    protected IDbContextFactory<JellyfinDbContext> CreateDbContextFactory() => Database.CreateDbContextFactory();

    protected BaseItemRepository CreateBaseItemRepository(ItemTypeLookup itemTypeLookup)
    {
        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        return new BaseItemRepository(
            CreateDbContextFactory(),
            new Mock<IServerApplicationHost>().Object,
            itemTypeLookup,
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Database.Dispose();
        }
    }
}

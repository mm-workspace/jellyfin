using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests;

/// <summary>
/// Covers a new server on the database provider selected for the test run.
/// </summary>
public sealed class FreshInstallTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellyfin-test-data", "fresh-install-" + System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.GetRandomFileName()));
    private string? _postgreSqlDatabaseName;

    [Fact]
    public async Task NewServer_AppliesEverySchemaMigrationAndSecondStartChangesNothing()
    {
        string[] firstHistory;
        using (var factory = new KeepDatabaseApplicationFactory(_root))
        {
            using var client = factory.CreateClient();
            _postgreSqlDatabaseName = factory.PostgreSqlDatabaseName;

            var provider = factory.Services.GetRequiredService<IJellyfinDatabaseProvider>();
            Assert.Equal(TestDatabase.SelectedProvider == TestDatabase.PostgreSql ? "PostgreSqlDatabaseProvider" : "SqliteDatabaseProvider", provider.GetType().Name);

            await using var context = await factory.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
            firstHistory = (await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Order(StringComparer.Ordinal).ToArray();
            Assert.All(context.GetService<IMigrationsAssembly>().Migrations.Keys, id => Assert.Contains(id, firstHistory));
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await context.Users.CountAsync(TestContext.Current.CancellationToken));
        }

        using (var factory = new KeepDatabaseApplicationFactory(_root))
        {
            using var client = factory.CreateClient();
            await using var context = await factory.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
            var secondHistory = (await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(firstHistory, secondHistory);
        }
    }

    public void Dispose()
    {
        if (_postgreSqlDatabaseName is not null)
        {
            JellyfinApplicationFactory.DropPostgreSqlDatabase(_postgreSqlDatabaseName);
        }

        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(_root, true);
        }
        catch (System.IO.IOException)
        {
            // Best effort, a locked file must not fail the test.
        }
    }

    private sealed class KeepDatabaseApplicationFactory : JellyfinApplicationFactory
    {
        public KeepDatabaseApplicationFactory(string webHostPathRoot)
            : base(webHostPathRoot)
        {
            KeepPostgreSqlDatabase = true;
        }
    }
}

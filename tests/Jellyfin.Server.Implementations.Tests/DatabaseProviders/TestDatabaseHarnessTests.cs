using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

public class TestDatabaseHarnessTests
{
    [Fact]
    public async Task Create_Database_HasSchemaAndCanBeReset()
    {
        await using var database = TestDatabase.Create();

        await using (var context = database.CreateDbContext())
        {
            context.ActivityLogs.Add(new ActivityLog("name", "type", Guid.NewGuid()));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = await database.CreateDbContextFactory().CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, await context.ActivityLogs.CountAsync(TestContext.Current.CancellationToken));
        }

        await database.ResetAsync(TestContext.Current.CancellationToken);

        await using (var context = database.CreateDbContext())
        {
            Assert.Equal(0, await context.ActivityLogs.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Create_Database_EnforcesForeignKeys()
    {
        await using var database = TestDatabase.Create();
        await using var context = database.CreateDbContext();

        context.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 1, 2, Guid.NewGuid()));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Create_UnknownProvider_Throws()
    {
        var previous = Environment.GetEnvironmentVariable(TestDatabase.ProviderEnvironmentVariable);
        Environment.SetEnvironmentVariable(TestDatabase.ProviderEnvironmentVariable, "unknown-provider");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => TestDatabase.Create());
            Assert.Contains("unknown-provider", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestDatabase.ProviderEnvironmentVariable, previous);
        }
    }
}

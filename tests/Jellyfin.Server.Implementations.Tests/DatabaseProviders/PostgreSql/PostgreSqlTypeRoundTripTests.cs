using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

[Trait("Provider", "PostgreSql")]
public sealed class PostgreSqlTypeRoundTripTests : IDisposable
{
    private readonly PostgreSqlTestDatabase? _database;

    public PostgreSqlTypeRoundTripTests()
    {
        if (TestDatabase.PostgreSqlConnectionString is { } connectionString)
        {
            _database = new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions());
        }
    }

    private PostgreSqlTestDatabase Database
    {
        get
        {
            Assert.SkipWhen(_database is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
            return _database;
        }
    }

    [Fact]
    public async Task KeyframeData_RoundTrips()
    {
        var itemId = Guid.NewGuid();
        await using (var context = Database.CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = itemId, Type = "Movie" });
            context.KeyframeData.Add(new KeyframeData { ItemId = itemId, TotalDuration = 5000, KeyframeTicks = [0, 1000, long.MaxValue] });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = Database.CreateDbContext())
        {
            var data = await context.KeyframeData.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal([0L, 1000L, long.MaxValue], data.KeyframeTicks!.ToArray());
        }
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task DateTime_AnyKind_IsStoredAsUtc(DateTimeKind kind)
    {
        var value = new DateTime(2024, 5, 1, 12, 34, 56, kind);
        var expected = value.ToUniversalTime();
        var id = await AddActivityLogAsync(value);

        await using var context = Database.CreateDbContext();
        var stored = (await context.ActivityLogs.SingleAsync(e => e.Id == id, TestContext.Current.CancellationToken)).DateCreated;
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        Assert.Equal(expected, stored);
    }

    [Fact]
    public async Task DateTime_Extremes_RoundTrip()
    {
        var minId = await AddActivityLogAsync(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
        var maxId = await AddActivityLogAsync(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));

        await using var context = Database.CreateDbContext();
        Assert.Equal(DateTime.MinValue, (await context.ActivityLogs.SingleAsync(e => e.Id == minId, TestContext.Current.CancellationToken)).DateCreated);
        Assert.Equal(DateTime.MaxValue, (await context.ActivityLogs.SingleAsync(e => e.Id == maxId, TestContext.Current.CancellationToken)).DateCreated);
    }

    [Fact]
    public async Task User_StaleUpdate_ThrowsConcurrencyException()
    {
        var user = new User("rowversion", "provider", "reset");
        await using (var context = Database.CreateDbContext())
        {
            context.Users.Add(user);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var first = Database.CreateDbContext();
        await using var second = Database.CreateDbContext();
        var firstUser = await first.Users.SingleAsync(e => e.Id.Equals(user.Id), TestContext.Current.CancellationToken);
        var secondUser = await second.Users.SingleAsync(e => e.Id.Equals(user.Id), TestContext.Current.CancellationToken);

        firstUser.Username = "first";
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        secondUser.Username = "second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LongString_OverMaxLength_IsStored()
    {
        var appName = new string('a', 10_000);
        await using (var context = Database.CreateDbContext())
        {
            var user = new User("longstrings", "provider", "reset");
            context.Users.Add(user);
            context.Devices.Add(new Device(user.Id, appName, "1.0", "device", Guid.NewGuid().ToString("N")));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = Database.CreateDbContext())
        {
            Assert.Equal(appName, (await context.Devices.SingleAsync(TestContext.Current.CancellationToken)).AppName);
        }
    }

    public void Dispose()
    {
        _database?.Dispose();
    }

    private async Task<int> AddActivityLogAsync(DateTime dateCreated)
    {
        await using var context = Database.CreateDbContext();
        var log = new ActivityLog("name", "type", Guid.NewGuid()) { DateCreated = dateCreated };
        context.ActivityLogs.Add(log);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return log.Id;
    }
}

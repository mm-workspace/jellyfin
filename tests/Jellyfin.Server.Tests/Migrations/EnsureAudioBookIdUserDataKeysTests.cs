using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using Jellyfin.Server.Migrations.Routines;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class EnsureAudioBookIdUserDataKeysTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _bookId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly Guid _songId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public EnsureAudioBookIdUserDataKeysTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Perform_AudioBookWithOnlySongKey_AddsIdKeyedCopy()
    {
        using (var ctx = CreateDbContext())
        {
            SeedUserAndItems(ctx);

            // A 10.10 row only carries the song key.
            ctx.UserData.Add(Row(_bookId, "0001Chapter 01", new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), playCount: 2, positionTicks: 600, rating: 4));
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await CreateMigration().PerformAsync(TestContext.Current.CancellationToken);

        using (var ctx = CreateDbContext())
        {
            var rows = ctx.UserData.Where(e => e.ItemId.Equals(_bookId)).OrderBy(e => e.CustomDataKey).ToList();
            Assert.Equal(["0001Chapter 01", _bookId.ToString()], rows.Select(e => e.CustomDataKey));
            Assert.All(rows, row =>
            {
                Assert.Equal(600, row.PlaybackPositionTicks);
                Assert.Equal(2, row.PlayCount);
                Assert.Equal(4, row.Rating);
                Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), row.LastPlayedDate);
            });
        }
    }

    [Fact]
    public async Task Perform_ConflictingSongKeyRows_CopiesMostRecentPlay()
    {
        using (var ctx = CreateDbContext())
        {
            SeedUserAndItems(ctx);
            ctx.UserData.Add(Row(_bookId, "0001Chapter 01", new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), playCount: 1, positionTicks: 100));
            ctx.UserData.Add(Row(_bookId, "Author-Book-0001Chapter 01", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), playCount: 3, positionTicks: 900));
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await CreateMigration().PerformAsync(TestContext.Current.CancellationToken);

        using (var ctx = CreateDbContext())
        {
            var twin = ctx.UserData.Single(e => e.ItemId.Equals(_bookId) && e.CustomDataKey == _bookId.ToString());
            Assert.Equal(900, twin.PlaybackPositionTicks);
            Assert.Equal(3, twin.PlayCount);
        }
    }

    [Fact]
    public async Task Perform_ExistingIdKeyMusicAndDetachedRows_AreLeftAlone()
    {
        using (var ctx = CreateDbContext())
        {
            SeedUserAndItems(ctx);

            // Already keyed by id, a music track, and a detached row.
            ctx.UserData.Add(Row(_bookId, "0001Chapter 01", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), playCount: 1, positionTicks: 700));
            ctx.UserData.Add(Row(_bookId, _bookId.ToString(), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), playCount: 1, positionTicks: 700));
            ctx.UserData.Add(Row(_songId, "0001Song", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), playCount: 5, positionTicks: 0));
            ctx.UserData.Add(Row(BaseItemRepository.PlaceholderId, "0002Chapter 02", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), playCount: 1, positionTicks: 300));
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await CreateMigration().PerformAsync(TestContext.Current.CancellationToken);

        using (var ctx = CreateDbContext())
        {
            Assert.Equal(2, ctx.UserData.Count(e => e.ItemId.Equals(_bookId)));
            Assert.Equal("0001Song", Assert.Single(ctx.UserData.Where(e => e.ItemId.Equals(_songId))).CustomDataKey);
            Assert.Equal("0002Chapter 02", Assert.Single(ctx.UserData.Where(e => e.ItemId.Equals(BaseItemRepository.PlaceholderId))).CustomDataKey);
        }
    }

    private void SeedUserAndItems(JellyfinDbContext ctx)
    {
        ctx.Users.Add(new User("user", "auth-provider", "reset-provider") { Id = _userId });
        ctx.BaseItems.Add(new BaseItemEntity { Id = _bookId, Type = typeof(AudioBook).FullName! });
        ctx.BaseItems.Add(new BaseItemEntity { Id = _songId, Type = typeof(Audio).FullName! });
    }

    private UserData Row(Guid itemId, string key, DateTime lastPlayed, int playCount, long positionTicks, double? rating = null)
    {
        return new UserData
        {
            ItemId = itemId,
            Item = null,
            UserId = _userId,
            User = null,
            CustomDataKey = key,
            LastPlayedDate = lastPlayed,
            PlayCount = playCount,
            PlaybackPositionTicks = positionTicks,
            Rating = rating
        };
    }

    private JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(new Mock<IApplicationPaths>().Object, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private EnsureAudioBookIdUserDataKeys CreateMigration()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);

        return new EnsureAudioBookIdUserDataKeys(
            new StartupLogger<EnsureAudioBookIdUserDataKeys>(NullLogger<EnsureAudioBookIdUserDataKeys>.Instance),
            factory.Object);
    }
}

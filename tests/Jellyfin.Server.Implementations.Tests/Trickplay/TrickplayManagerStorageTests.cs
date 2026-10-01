using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Trickplay;

/// <summary>
/// Covers how the tile info of an item is stored and removed. The lookup the save starts from is a read, so the write
/// lock that queues writes inside one server does not cover it, and two callers here can miss each other's row just as
/// a second server on the same database can. The other writer stores its row between what the save it competes with
/// reads and what it writes, which is where a second server's commit falls.
/// </summary>
public sealed class TrickplayManagerStorageTests : IDisposable
{
    private const int Width = 320;

    private static readonly Guid _itemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly CompetingWriter _otherWriter = new();
    private readonly ITestDatabase _database;

    public TrickplayManagerStorageTests()
    {
        _database = TestDatabase.Create(new TestDatabaseOptions
        {
            ApplicationPaths = Mock.Of<IApplicationPaths>(),
            Interceptors = [_otherWriter]
        });
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task SaveTrickplayInfo_WhenAnotherWriterStoredTheSameWidthFirst_StoresTheInfoItWasGiven()
    {
        var manager = CreateManager(_database.CreateDbContextFactory());
        var otherManager = CreateManager(_database.CreateDbContextFactory());
        _otherWriter.ArmAsync(() => otherManager.SaveTrickplayInfo(CreateInfo(height: 7)));

        await manager.SaveTrickplayInfo(CreateInfo(height: 9));

        Assert.True(_otherWriter.Wrote, "The other writer never got in, so the insert was not made to meet a stored row.");

        using var context = _database.CreateDbContext();
        var stored = Assert.Single(context.TrickplayInfos);
        Assert.Equal(9, stored.Height);
    }

    [Fact]
    public async Task SaveTrickplayInfo_WhenTheItemAlreadyHasThatWidth_ReplacesTheStoredInfo()
    {
        var manager = CreateManager(_database.CreateDbContextFactory());

        await manager.SaveTrickplayInfo(CreateInfo(height: 7));
        await manager.SaveTrickplayInfo(CreateInfo(height: 9));

        using var context = _database.CreateDbContext();
        var stored = Assert.Single(context.TrickplayInfos);
        Assert.Equal(9, stored.Height);
    }

    [Fact]
    public async Task DeleteTrickplayDataAsync_DisposesTheContextItRead()
    {
        var contexts = new TrackedContexts(_database);
        var manager = CreateManager(contexts);
        await manager.SaveTrickplayInfo(CreateInfo(height: 7));

        await manager.DeleteTrickplayDataAsync(_itemId, TestContext.Current.CancellationToken);

        using var context = _database.CreateDbContext();
        Assert.Empty(context.TrickplayInfos);
        Assert.NotEmpty(contexts.Created);
        Assert.All(contexts.Created, created => Assert.True(created.Disposed, "A context was left open."));
    }

    private static TrickplayInfo CreateInfo(int height) => new()
    {
        ItemId = _itemId,
        Width = Width,
        Height = height,
        TileWidth = 10,
        TileHeight = 10,
        ThumbnailCount = 100,
        Interval = 10000,
        Bandwidth = 1000
    };

    private TrickplayManager CreateManager(IDbContextFactory<JellyfinDbContext> dbContextFactory)
    {
        // Only the database is reached by what is covered here; the encoding and file system parts are not.
        return new TrickplayManager(
            NullLogger<TrickplayManager>.Instance,
            null!,
            null!,
            null!,
            null!,
            null!,
            dbContextFactory,
            null!,
            null!,
            _database.Provider);
    }

    /// <summary>
    /// A context factory that keeps what it handed out, so a caller that leaves a context open can be told from one
    /// that disposes it.
    /// </summary>
    private sealed class TrackedContexts : IDbContextFactory<JellyfinDbContext>
    {
        private readonly ITestDatabase _database;
        private readonly List<TrackedContext> _created = [];

        public TrackedContexts(ITestDatabase database)
        {
            _database = database;
        }

        /// <summary>
        /// Gets the contexts handed out so far.
        /// </summary>
        public IReadOnlyList<TrackedContext> Created => _created;

        /// <inheritdoc />
        public JellyfinDbContext CreateDbContext()
        {
            var context = new TrackedContext(_database);
            _created.Add(context);
            return context;
        }
    }

    /// <summary>
    /// A context that records having been disposed.
    /// </summary>
    private sealed class TrackedContext : JellyfinDbContext
    {
        public TrackedContext(ITestDatabase database)
            : base(database.Options, NullLogger<JellyfinDbContext>.Instance, database.Provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
        {
        }

        /// <summary>
        /// Gets a value indicating whether this context was disposed.
        /// </summary>
        public bool Disposed { get; private set; }

        /// <inheritdoc />
        public override void Dispose()
        {
            Disposed = true;
            base.Dispose();
        }

        /// <inheritdoc />
        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }
}

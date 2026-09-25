using System;
using System.Linq;
using System.Threading;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Ratings and gains that are not real numbers have no way through the stack: SQLite refuses to store NaN at
/// all, PostgreSQL stores it and then sorts it above every genuine rating, and neither NaN nor infinity can be
/// written as JSON. They are dropped at the database boundary so that no provider is asked to hold one, and so
/// that a row that already does cannot fail the responses it appears in.
/// </summary>
public sealed class StoredRatingSanitizationTests : DbTestFixture
{
    private readonly ItemTypeLookup _itemTypeLookup = new();
    private readonly ILibraryManager? _previousLibraryManager;
    private readonly IServerConfigurationManager? _previousConfigurationManager;

    public StoredRatingSanitizationTests()
    {
        // BaseItem resolves these through process-wide statics; restored in Dispose.
        _previousLibraryManager = BaseItem.LibraryManager;
        _previousConfigurationManager = BaseItem.ConfigurationManager;

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        BaseItem.LibraryManager = libraryManager.Object;

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        BaseItem.ConfigurationManager = configurationManager.Object;
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void SaveItems_NonFiniteRatings_StoresNoValueForThem(float value)
    {
        var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        SaveItem(new Book
        {
            Id = id,
            Name = "Rated",
            CommunityRating = value,
            CriticRating = value,
            LUFS = value,
            NormalizationGain = value
        });

        using var context = CreateDbContext();
        var item = context.BaseItems.Single(e => e.Id.Equals(id));
        Assert.Null(item.CommunityRating);
        Assert.Null(item.CriticRating);
        Assert.Null(item.LUFS);
        Assert.Null(item.NormalizationGain);
    }

    [Fact]
    public void SaveItems_RealRatings_StoresThem()
    {
        var id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        SaveItem(new Book { Id = id, Name = "Rated", CommunityRating = 7.5f, CriticRating = 82f });

        using var context = CreateDbContext();
        var item = context.BaseItems.Single(e => e.Id.Equals(id));
        Assert.Equal(7.5f, item.CommunityRating);
        Assert.Equal(82f, item.CriticRating);
    }

    [Fact]
    public void RetrieveItem_StoredInfiniteRatings_ReadsNoValueForThem()
    {
        // Infinity is what a database can already be holding: unlike NaN, every provider stores it happily.
        var id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity
            {
                Id = id,
                Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Book],
                Name = "Rated",
                CommunityRating = float.PositiveInfinity,
                CriticRating = float.NegativeInfinity
            });
            context.SaveChanges();
        }

        var item = CreateBaseItemRepository(_itemTypeLookup).RetrieveItem(id);

        Assert.NotNull(item);
        Assert.Null(item.CommunityRating);
        Assert.Null(item.CriticRating);
    }

    protected override void Dispose(bool disposing)
    {
        BaseItem.LibraryManager = _previousLibraryManager!;
        BaseItem.ConfigurationManager = _previousConfigurationManager!;
        base.Dispose(disposing);
    }

    private void SaveItem(BaseItem item)
        => new ItemPersistenceService(
                CreateDbContextFactory(),
                new Mock<IServerApplicationHost>().Object,
                Database.Provider,
                NullLogger<ItemPersistenceService>.Instance)
            .SaveItems([item], CancellationToken.None);
}

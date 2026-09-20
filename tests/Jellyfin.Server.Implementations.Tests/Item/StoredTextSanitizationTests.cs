using System;
using System.Linq;
using System.Threading;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Text that no database can be relied on to store is sanitized on the way in, so that every provider keeps
/// the same characters. A null character is dropped and a surrogate without its pair becomes U+FFFD, which is
/// already what SQLite's UTF-8 encoder substitutes for it.
/// </summary>
public sealed class StoredTextSanitizationTests : DbTestFixture
{
    // Lone surrogates cannot go through attribute arguments: metadata strings are UTF-8, so the compiler
    // writes U+FFFD in their place and a theory case would assert nothing.
    private const string Unstorable = "Bad\0Text\uD83C";
    private const string Sanitized = "BadText�";

    private static readonly Guid _itemId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly ItemTypeLookup _itemTypeLookup = new();
    private readonly ILibraryManager? _previousLibraryManager;
    private readonly IServerConfigurationManager? _previousConfigurationManager;

    public StoredTextSanitizationTests()
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

        using var context = CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = _itemId,
            Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie],
            Name = "Movie",
            MediaType = "Video"
        });
        context.SaveChanges();
    }

    [Fact]
    public void SaveChapters_UnstorableText_StoresTheSanitizedText()
    {
        var repository = new ChapterRepository(CreateDbContextFactory(), new Mock<IImageProcessor>().Object);

        repository.SaveChapters(_itemId, [new ChapterInfo { Name = Unstorable, ImagePath = Unstorable }]);

        using var context = CreateDbContext();
        var chapter = Assert.Single(context.Chapters);
        Assert.Equal(Sanitized, chapter.Name);
        Assert.Equal(Sanitized, chapter.ImagePath);
    }

    [Fact]
    public void SaveMediaStreams_UnstorableText_StoresTheSanitizedText()
    {
        var repository = new MediaStreamRepository(
            CreateDbContextFactory(),
            new Mock<IServerApplicationHost>().Object,
            new Mock<ILocalizationManager>().Object);

        repository.SaveMediaStreams(
            _itemId,
            [new MediaStream { Index = 0, Type = MediaStreamType.Video, Title = Unstorable, Comment = Unstorable, Path = Unstorable }],
            CancellationToken.None);

        using var context = CreateDbContext();
        var stream = Assert.Single(context.MediaStreamInfos);
        Assert.Equal(Sanitized, stream.Title);
        Assert.Equal(Sanitized, stream.Comment);
        Assert.Equal(Sanitized, stream.Path);
    }

    [Fact]
    public void SaveMediaAttachments_UnstorableText_StoresTheSanitizedText()
    {
        var repository = new MediaAttachmentRepository(CreateDbContextFactory());

        repository.SaveMediaAttachments(
            _itemId,
            [new MediaAttachment { Index = 0, FileName = Unstorable, Comment = Unstorable, MimeType = Unstorable }],
            CancellationToken.None);

        using var context = CreateDbContext();
        var attachment = Assert.Single(context.AttachmentStreamInfos);
        Assert.Equal(Sanitized, attachment.Filename);
        Assert.Equal(Sanitized, attachment.Comment);
        Assert.Equal(Sanitized, attachment.MimeType);
    }

    [Fact]
    public void UpdatePeople_UnstorableText_StoresTheSanitizedText()
    {
        var repository = new PeopleRepository(CreateDbContextFactory(), _itemTypeLookup, new Mock<IItemQueryHelpers>().Object);

        repository.UpdatePeople(_itemId, [new PersonInfo { Name = Unstorable, Type = PersonKind.Actor, Role = Unstorable }]);

        using var context = CreateDbContext();
        Assert.Equal(Sanitized, Assert.Single(context.Peoples).Name);
        Assert.Equal(Sanitized, Assert.Single(context.PeopleBaseItemMap).Role);
    }

    [Fact]
    public void SaveItems_UnstorableText_StoresTheSanitizedTextInTheItemAndItsValues()
    {
        var service = new ItemPersistenceService(
            CreateDbContextFactory(),
            new Mock<IServerApplicationHost>().Object,
            NullLogger<ItemPersistenceService>.Instance);
        var id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        service.SaveItems(
            [new Book { Id = id, Name = Unstorable, Overview = Unstorable, Tags = [Unstorable] }],
            CancellationToken.None);

        using var context = CreateDbContext();
        var item = context.BaseItems.Single(e => e.Id.Equals(id));
        Assert.Equal(Sanitized, item.Name);
        Assert.Equal(Sanitized, item.Overview);
        Assert.Equal(Sanitized, item.Tags);
        Assert.Contains(Sanitized, context.ItemValues.Select(e => e.Value).ToArray());
    }

    [Fact]
    public void SaveItems_TagOfOnlyUnstorableText_StoresNoValueForIt()
    {
        var service = new ItemPersistenceService(
            CreateDbContextFactory(),
            new Mock<IServerApplicationHost>().Object,
            NullLogger<ItemPersistenceService>.Instance);
        var id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        // Nothing of it can be stored, so it is as blank as a tag of spaces, which is dropped.
        service.SaveItems([new Book { Id = id, Name = "Book", Tags = ["\0\0"] }], CancellationToken.None);

        using var context = CreateDbContext();
        Assert.DoesNotContain(string.Empty, context.ItemValues.Select(e => e.Value).ToArray());
        Assert.Empty(context.ItemValuesMap.Where(e => e.ItemId.Equals(id)));
    }

    protected override void Dispose(bool disposing)
    {
        BaseItem.LibraryManager = _previousLibraryManager!;
        BaseItem.ConfigurationManager = _previousConfigurationManager!;
        base.Dispose(disposing);
    }
}

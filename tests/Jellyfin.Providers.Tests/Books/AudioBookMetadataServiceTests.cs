using System;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.Books;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.Books;

// put tests that mock the static LibraryManager in the same collection to avoid test interference
[Collection("LibraryManagerTests")]
public sealed class AudioBookMetadataServiceTests : IDisposable
{
    private readonly TestAudioBookMetadataService _service = new();
    private readonly ILibraryManager? _previousLibraryManager;

    public AudioBookMetadataServiceTests()
    {
        _previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = Mock.Of<ILibraryManager>();
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = _previousLibraryManager;
    }

    [Theory]
    [InlineData("Discworld", null, false, "Discworld")]
    [InlineData("Discworld", "Old Series", false, "Old Series")]
    [InlineData("Discworld", "Old Series", true, "Discworld")]
    [InlineData(null, "Old Series", false, "Old Series")]
    public void MergeData_SeriesName_MergesLikeBooks(string? sourceSeries, string? targetSeries, bool replaceData, string expected)
    {
        var source = new MetadataResult<AudioBook> { Item = new AudioBook { SeriesName = sourceSeries } };
        var target = new MetadataResult<AudioBook> { Item = new AudioBook { SeriesName = targetSeries } };

        _service.Merge(source, target, replaceData);

        Assert.Equal(expected, target.Item.SeriesName);
    }

    private sealed class TestAudioBookMetadataService : AudioBookMetadataService
    {
        public TestAudioBookMetadataService()
            : base(
                Mock.Of<IServerConfigurationManager>(),
                NullLogger<AudioBookMetadataService>.Instance,
                Mock.Of<IProviderManager>(),
                Mock.Of<IFileSystem>(),
                Mock.Of<ILibraryManager>(),
                Mock.Of<IExternalDataManager>(),
                Mock.Of<IItemRepository>())
        {
        }

        public void Merge(MetadataResult<AudioBook> source, MetadataResult<AudioBook> target, bool replaceData)
        {
            MergeData(source, target, Array.Empty<MetadataField>(), replaceData, true);
        }
    }
}

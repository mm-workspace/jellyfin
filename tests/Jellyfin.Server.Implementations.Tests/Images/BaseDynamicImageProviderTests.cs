using System;
using System.Collections.Generic;
using Emby.Server.Implementations.Images;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Images;

public class BaseDynamicImageProviderTests
{
    [Theory]
    [InlineData(5, false)]
    [InlineData(5000, false)]
    [InlineData(-5000, false)]
    [InlineData(TimeSpan.TicksPerSecond * 2, true)]
    public void HasChangedByDate_AllowsASecondOfDifference(long ticks, bool expected)
    {
        // A database can store the date with microsecond precision while the file system reports 100 nanosecond ticks.
        var storedTime = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetLastWriteTimeUtc("/images/folder.jpg")).Returns(storedTime.AddTicks(ticks));
        var provider = new TestImageProvider(fileSystem.Object);

        var changed = provider.HasChangedByDateOf(new Folder(), new ItemImageInfo { Path = "/images/folder.jpg", DateModified = storedTime });

        Assert.Equal(expected, changed);
    }

    private sealed class TestImageProvider(IFileSystem fileSystem)
        : BaseDynamicImageProvider<Folder>(fileSystem, Mock.Of<IProviderManager>(), Mock.Of<IApplicationPaths>(), Mock.Of<IImageProcessor>())
    {
        public bool HasChangedByDateOf(BaseItem item, ItemImageInfo image) => HasChangedByDate(item, image);

        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item) => [];
    }
}

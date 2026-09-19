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
    public static TheoryData<DateTime, DateTime, bool> GetStoredAndFileDates()
    {
        // Npgsql writes whole microseconds counted from 2000-01-01 and drops the rest towards that date,
        // so the stored date is up to 9 ticks earlier than the file's after 2000 and up to 9 ticks later before it.
        var after2000 = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        var before2000 = new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(1_234_560);

        return new TheoryData<DateTime, DateTime, bool>
        {
            { after2000, after2000.AddTicks(9), false },
            { after2000, after2000.AddTicks(TimeSpan.TicksPerMicrosecond), true },
            { after2000, after2000.AddMilliseconds(500), true },
            { before2000.AddTicks(TimeSpan.TicksPerMicrosecond), before2000.AddTicks(1), false },
            { before2000.AddTicks(TimeSpan.TicksPerMicrosecond), before2000, true },
            { before2000, before2000.AddMilliseconds(-500), true }
        };
    }

    [Theory]
    [MemberData(nameof(GetStoredAndFileDates))]
    public void HasChangedByDate_ChangedOnlyIfTimeDiffersByAMicrosecond(DateTime storedDate, DateTime fileDate, bool expected)
    {
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetLastWriteTimeUtc("/images/folder.jpg")).Returns(fileDate);
        var provider = new TestImageProvider(fileSystem.Object);

        var changed = provider.HasChangedByDateOf(new Folder(), new ItemImageInfo { Path = "/images/folder.jpg", DateModified = storedDate });

        Assert.Equal(expected, changed);
    }

    private sealed class TestImageProvider(IFileSystem fileSystem)
        : BaseDynamicImageProvider<Folder>(fileSystem, Mock.Of<IProviderManager>(), Mock.Of<IApplicationPaths>(), Mock.Of<IImageProcessor>())
    {
        public bool HasChangedByDateOf(BaseItem item, ItemImageInfo image) => HasChangedByDate(item, image);

        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item) => [];
    }
}

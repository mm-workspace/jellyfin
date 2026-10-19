using System;
using System.Collections.Generic;
using Emby.Naming.Common;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.MediaInfo;

public class AudioFileProberChapterTests
{
    private static readonly long _runTimeTicks = TimeSpan.FromHours(1).Ticks;

    private readonly Mock<IChapterManager> _chapterManager = new();
    private readonly AudioFileProber _audioFileProber;

    public AudioFileProberChapterTests()
    {
        var mediaEncoder = Mock.Of<IMediaEncoder>();
        _audioFileProber = new AudioFileProber(
            NullLogger<AudioFileProber>.Instance,
            Mock.Of<IMediaSourceManager>(),
            mediaEncoder,
            Mock.Of<ILibraryManager>(),
            new LyricResolver(
                NullLogger<LyricResolver>.Instance,
                Mock.Of<ILocalizationManager>(),
                mediaEncoder,
                Mock.Of<IFileSystem>(),
                new NamingOptions()),
            Mock.Of<ILyricManager>(),
            Mock.Of<IMediaStreamRepository>(),
            _chapterManager.Object);
    }

    [Fact]
    public void SaveAudioBookChapters_FileLostItsChapters_ClearsStoredChapters()
    {
        var book = Save(stored: [Chapter("Opening", 0)], probed: []);

        _chapterManager.Verify(m => m.SaveChapters(book, It.Is<IReadOnlyList<ChapterInfo>>(c => c.Count == 0)), Times.Once);
    }

    [Fact]
    public void SaveAudioBookChapters_NoChapters_DoesNotSaveChapters()
    {
        Save(stored: [], probed: []);

        _chapterManager.Verify(m => m.SaveChapters(It.IsAny<BaseItem>(), It.IsAny<IReadOnlyList<ChapterInfo>>()), Times.Never);
    }

    [Fact]
    public void SaveAudioBookChapters_ChaptersUnchanged_KeepsStoredChapters()
    {
        // Stored chapters can carry images the probe knows nothing about.
        var stored = Chapter("Opening", 0);
        stored.ImagePath = "/metadata/chapters/opening.jpg";

        Save(
            stored: [stored, Chapter("Chapter One", 600)],
            probed: [Chapter("Opening", 0), Chapter("Chapter One", 600), Chapter("Past the end", _runTimeTicks)]);

        _chapterManager.Verify(m => m.SaveChapters(It.IsAny<BaseItem>(), It.IsAny<IReadOnlyList<ChapterInfo>>()), Times.Never);
    }

    [Theory]
    [InlineData("Opening", 0, "Prologue", 0)]
    [InlineData("Opening", 0, "Opening", 50)]
    public void SaveAudioBookChapters_ChaptersChanged_SavesProbedChapters(string storedName, long storedStart, string probedName, long probedStart)
    {
        var book = Save(stored: [Chapter(storedName, storedStart)], probed: [Chapter(probedName, probedStart)]);

        _chapterManager.Verify(
            m => m.SaveChapters(book, It.Is<IReadOnlyList<ChapterInfo>>(c => c.Count == 1 && c[0].Name == probedName && c[0].StartPositionTicks == probedStart)),
            Times.Once);
    }

    private static ChapterInfo Chapter(string name, long startPositionTicks)
        => new() { Name = name, StartPositionTicks = startPositionTicks };

    private AudioBook Save(ChapterInfo[] stored, ChapterInfo[] probed)
    {
        var book = new AudioBook { Id = Guid.NewGuid(), RunTimeTicks = _runTimeTicks };
        _chapterManager.Setup(m => m.GetChapters(book.Id)).Returns(stored);

        _audioFileProber.SaveAudioBookChapters(book, new MediaBrowser.Model.MediaInfo.MediaInfo { Chapters = probed });

        return book;
    }
}

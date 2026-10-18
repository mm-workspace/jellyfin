using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace Jellyfin.Controller.Tests.Entities;

public class AudioBookTests
{
    private static readonly Guid _id = Guid.Parse("abababab-abab-abab-abab-abababababab");

    [Theory]
    [InlineData(null, null)]
    [InlineData("The Book", null)]
    [InlineData(null, "The Author")]
    public void GetUserDataKeys_WithoutAlbumAndAlbumArtist_ReturnsOnlyId(string? album, string? albumArtist)
    {
        var book = new AudioBook
        {
            Id = _id,
            Name = "Chapter 01",
            IndexNumber = 1,
            Album = album,
            AlbumArtists = albumArtist is null ? [] : [albumArtist]
        };

        Assert.Equal([_id.ToString()], book.GetUserDataKeys());
    }

    [Fact]
    public void GetUserDataKeys_WithAlbumAndAlbumArtist_MatchesAudioKeys()
    {
        var book = new AudioBook { Id = _id, Name = "Chapter 01", IndexNumber = 1, Album = "The Book", AlbumArtists = ["The Author"] };
        var audio = new Audio { Id = _id, Name = "Chapter 01", IndexNumber = 1, Album = "The Book", AlbumArtists = ["The Author"] };

        Assert.Equal(["The Author-The Book-0001Chapter 01", _id.ToString()], book.GetUserDataKeys());
        Assert.Equal(audio.GetUserDataKeys(), book.GetUserDataKeys());
    }
}

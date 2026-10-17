using Kinora.Core;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

public class MediaFilesTests
{
    [Theory]
    [InlineData("12 Strong.en.srt", "12 Strong", "en")]
    [InlineData("1 Night.eng.srt", "1 Night", "en")]
    [InlineData("12 Strong.srt", "12 Strong", "")]
    [InlineData("Black Phone 2.en.forced.srt", "Black Phone 2", "en forced")]
    [InlineData("Movie.ita.srt", "Movie", "it")]
    [InlineData("Movie (2025).eng.srt", "Movie", null)]
    [InlineData("Movie 2.srt", "Movie", null)]
    [InlineData("Movie.nfo", "Movie", null)]
    public void Subtitle_tags(string file, string video, string? expected) =>
        Assert.Equal(expected, MediaFiles.SubtitleTag(file, video));

    [Theory]
    [InlineData("Movie-trailer.mp4", true)]
    [InlineData("Movie.sample.mkv", true)]
    [InlineData("sample.mkv", true)]
    [InlineData("Scene of the Crime.mkv", false)]
    [InlineData("Short Circuit.mkv", false)]
    [InlineData("12 Strong.mp4", false)]
    public void Extras(string file, bool extra) => Assert.Equal(extra, MediaFiles.IsExtraVideo(file));

    [Fact]
    public void Artwork_in_a_movie_folder()
    {
        var files = new[] { "12 Strong-fanart.jpg", "12 Strong.tbn", "folder.jpg", "poster.jpg", "fanart.jpg", "logo.png", "landscape.jpg" };
        var art = LocalArtwork.Find(files, "12 Strong", ownFolder: true);
        Assert.Equal("poster.jpg", art.Poster);
        Assert.Equal("fanart.jpg", art.Fanart);
        Assert.Equal("logo.png", art.Logo);

        var kodiOnly = LocalArtwork.Find(["Heat.tbn", "Heat-fanart.jpg"], "Heat", ownFolder: true);
        Assert.Equal("Heat.tbn", kodiOnly.Poster);
        Assert.Equal("Heat-fanart.jpg", kodiOnly.Fanart);
    }

    [Fact]
    public void Artwork_of_a_loose_video_ignores_the_folder_pictures()
    {
        var art = LocalArtwork.Find(["poster.jpg", "Heat-poster.jpg", "Alien-poster.jpg"], "Heat", ownFolder: false);
        Assert.Equal("Heat-poster.jpg", art.Poster);
        Assert.Null(art.Fanart);
    }

    [Theory]
    [InlineData("Amélie", "amelie")]
    [InlineData("Spider-Man: No Way Home", "spider man no way home")]
    [InlineData("Assassin's Creed", "assassins creed")]
    public void Search_text(string text, string normalized) => Assert.Equal(normalized, SearchText.Normalize(text));

    [Fact]
    public void Search_matches_every_word_anywhere()
    {
        var text = SearchText.Normalize("A Haunting in Venice 2023 Kenneth Branagh");
        Assert.True(SearchText.Matches(text, SearchText.Normalize("venice branagh")));
        Assert.False(SearchText.Matches(text, SearchText.Normalize("venice poirot")));
    }
}

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

    [Fact]
    public void Subtitles_are_looked_for_in_the_Subs_folder_next_to_the_video()
    {
        var root = Directory.CreateTempSubdirectory("kinora-subs-");
        try
        {
            var withSubs = Directory.CreateDirectory(Path.Combine(root.FullName, "Crime 101 (2026)"));
            Directory.CreateDirectory(Path.Combine(withSubs.FullName, ".actors"));
            var subs = Directory.CreateDirectory(Path.Combine(withSubs.FullName, "subs"));
            var plain = Directory.CreateDirectory(Path.Combine(root.FullName, "Heat (1995)"));

            Assert.Equal(subs.FullName, MediaFiles.SubtitleFolder(Path.Combine(withSubs.FullName, "Crime 101 (2026).mp4")));
            Assert.Equal(plain.FullName, MediaFiles.SubtitleFolder(Path.Combine(plain.FullName, "Heat (1995).mkv")));
            Assert.Equal(subs.FullName, MediaFiles.SubtitleFolder(new Uri(Path.Combine(withSubs.FullName, "Crime 101 (2026).mp4")).AbsoluteUri));
            Assert.Null(MediaFiles.SubtitleFolder("http://192.168.1.10:8096/Videos/12/stream.mkv"));
            Assert.Null(MediaFiles.SubtitleFolder(Path.Combine(root.FullName, "Gone", "Gone.mkv")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("Amélie", "amelie")]
    [InlineData("Spider-Man: No Way Home", "spider man no way home")]
    [InlineData("Assassin's Creed", "assassins creed")]
    public void Search_text(string text, string normalized) => Assert.Equal(normalized, SearchText.Normalize(text));

    [Fact]
    public void Titles_sort_without_the_article_in_front_unless_asked_to_keep_it()
    {
        static List<string> Sorted(bool ignore, params string[] titles) =>
            titles.OrderBy(t => SearchText.SortKey(t, ignore), StringComparer.Ordinal).ToList();

        string[] titles = ["Up", "The Matrix", "A Quiet Place", "Heat", "An American Werewolf in London", "The"];
        Assert.Equal(["An American Werewolf in London", "Heat", "The Matrix", "A Quiet Place", "The", "Up"], Sorted(true, titles));
        Assert.Equal(["A Quiet Place", "An American Werewolf in London", "Heat", "The", "The Matrix", "Up"], Sorted(false, titles));
        Assert.Equal("matrix", SearchText.SortKey("The Matrix"));
        Assert.Equal("there will be blood", SearchText.SortKey("There Will Be Blood"));   // a word, not the article
        Assert.Equal("la dolce vita", SearchText.SortKey("La dolce vita"));
    }

    [Fact]
    public void A_short_title_comes_before_everything_else_with_its_letters()
    {
        static int Rank(string query, params string[] titles) =>
            SearchText.Rank(titles.Select(SearchText.Normalize), SearchText.Normalize(query));

        Assert.Equal(0, Rank("her", "Her"));
        Assert.Equal(0, Rank("thing", "The Thing"));
        Assert.Equal(0, Rank("amelie", "Le fabuleux destin d'Amélie Poulain", "Amélie"));   // the original title counts too
        Assert.Equal(1, Rank("her", "Hereditary"));
        Assert.Equal(1, Rank("her", "The Hero"));
        Assert.Equal(2, Rank("her", "Kill Her Goats"));
        Assert.Equal(2, Rank("dark knight", "Batman: The Dark Knight Returns"));
        Assert.Equal(3, Rank("her", "The Others"));
        Assert.Equal(3, Rank("her", "Sherlock Holmes"));
        Assert.Equal(3, Rank("her", "Heat"));   // found in the cast, not in the title
    }

    [Fact]
    public void Search_matches_every_word_anywhere()
    {
        var text = SearchText.Normalize("A Haunting in Venice 2023 Kenneth Branagh");
        Assert.True(SearchText.Matches(text, SearchText.Normalize("venice branagh")));
        Assert.False(SearchText.Matches(text, SearchText.Normalize("venice poirot")));
    }
}

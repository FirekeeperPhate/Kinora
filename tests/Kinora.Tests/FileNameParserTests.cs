using Kinora.Core.Scanning;

namespace Kinora.Tests;

public class FileNameParserTests
{
    [Theory]
    [InlineData("12 Strong (2018)", "12 Strong", 2018)]
    [InlineData("1 Night", "1 Night", null)]
    [InlineData("1917", "1917", null)]
    [InlineData("1917 (2019)", "1917", 2019)]
    [InlineData("I.T", "I.T", null)]
    [InlineData("Blade.Runner.2049.2017.1080p.BluRay.x264", "Blade Runner 2049", 2017)]
    [InlineData("The.Matrix.1999.2160p.UHD.BluRay.REMUX", "The Matrix", 1999)]
    [InlineData("Abraham's Boys - A Dracula Story (2025)", "Abraham's Boys - A Dracula Story", 2025)]
    [InlineData("Alien_1979_720p", "Alien", 1979)]
    [InlineData("2001 A Space Odyssey 1968", "2001 A Space Odyssey", 1968)]
    public void Title_and_year(string name, string title, int? year)
    {
        var parsed = FileNameParser.Parse(name);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void Ids_in_the_name_are_read_and_removed()
    {
        var parsed = FileNameParser.Parse("Heat (1995) [imdbid-tt0113277] {tmdb-949}");
        Assert.Equal("Heat", parsed.Title);
        Assert.Equal(1995, parsed.Year);
        Assert.Equal("tt0113277", parsed.ImdbId);
        Assert.Equal("949", parsed.TmdbId);
    }
}

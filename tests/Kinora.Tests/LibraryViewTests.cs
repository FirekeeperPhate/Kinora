using Kinora.Core;
using Kinora.Core.Models;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

public class LibraryViewTests
{
    private static TitleIds Ids(string title, int? year, string? imdb = null, string? tmdb = null, string? tvdb = null) =>
        new(imdb, tmdb, tvdb, title, year);

    private static List<List<string>> Groups(params TitleIds[] items) =>
        DuplicateFinder.Groups(items, i => i).Select(g => g.Select(i => i.Title).ToList()).ToList();

    [Fact]
    public void An_id_in_common_is_a_duplicate_whatever_the_name()
    {
        var groups = Groups(Ids("Alien", 1979, imdb: "tt1"), Ids("Aliens", 1986, imdb: "tt2"), Ids("Alien (Director's Cut)", 2003, imdb: "tt1"));
        Assert.Equal([["Alien", "Alien (Director's Cut)"]], groups);
    }

    [Fact]
    public void Two_ids_chain_three_copies()
    {
        var groups = Groups(Ids("A", 2000, imdb: "tt1"), Ids("B", 2000, imdb: "tt1", tmdb: "5"), Ids("C", 2001, tmdb: "5"));
        Assert.Equal([["A", "B", "C"]], groups);
    }

    [Fact]
    public void The_same_name_and_year_counts_only_without_ids()
    {
        Assert.Equal([["Dune", "dune"]], Groups(Ids("Dune", 2021), Ids("dune", 2021)));
        Assert.Equal([["Dune", "Dune"]], Groups(Ids("Dune", 2021, imdb: "tt1"), Ids("Dune", 2021)));
        Assert.Empty(Groups(Ids("Dune", 2021, imdb: "tt1"), Ids("Dune", 2021, imdb: "tt2")));   // namesakes
        Assert.Empty(Groups(Ids("Dune", 1984), Ids("Dune", 2021)));
        Assert.Empty(Groups(Ids("Hamlet", null), Ids("Hamlet", null)));                         // too little to say
    }

    [Fact]
    public void One_without_ids_goes_with_one_namesake_and_does_not_join_them()
    {
        var groups = Groups(Ids("Dune", 2021, imdb: "tt1"), Ids("Dune", 2021, imdb: "tt2"), Ids("Dune", 2021));
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void Filters()
    {
        var movie = new Movie { Year = 1994, Rating = 7.5, Resolution = "1080p", Collection = "Bond", VideoFile = "m.mkv" };

        Assert.True(new LibraryFilter().Allows(movie));
        Assert.True(new LibraryFilter { Decade = 1990, MinRating = 7, Resolution = "1080p", Collection = "bond" }.Allows(movie));
        Assert.False(new LibraryFilter { Decade = 1980 }.Allows(movie));
        Assert.False(new LibraryFilter { MinRating = 8 }.Allows(movie));
        Assert.False(new LibraryFilter { Resolution = "4K" }.Allows(movie));
        Assert.False(new LibraryFilter { Collection = "Star Wars" }.Allows(movie));
        Assert.False(new LibraryFilter { Hdr = true }.Allows(movie));

        var unknown = new Movie { VideoFile = "m.mkv" };
        Assert.False(new LibraryFilter { Decade = 1990 }.Allows(unknown));
        Assert.False(new LibraryFilter { MinRating = 6 }.Allows(unknown));
        Assert.False(new LibraryFilter { Resolution = LibraryFilter.LowerResolution }.Allows(unknown));
        Assert.True(new LibraryFilter { Resolution = LibraryFilter.LowerResolution }.Allows(new Movie { Resolution = "SD" }));
        Assert.True(new LibraryFilter { Resolution = LibraryFilter.LowerResolution }.Allows(new Movie { Resolution = "576p" }));
    }

    [Theory]
    [InlineData("Movie.2023.2160p.HDR.x265.mkv", true)]
    [InlineData("Movie.2023.2160p.HDR10+.mkv", true)]
    [InlineData("Movie (2023) - DoVi.mkv", true)]
    [InlineData("Movie Dolby Vision.mkv", true)]
    [InlineData("Movie.2023.2160p.WEB-DL.DV.H265.mkv", true)]
    [InlineData("The DV Files.mkv", false)]
    [InlineData("Hdrezka.mkv", false)]
    [InlineData("Movie.2023.1080p.mkv", false)]
    public void Hdr_in_the_name_of_the_file(string file, bool hdr) =>
        Assert.Equal(hdr, new Movie { VideoFile = "folder/" + file }.IsHdr);

    [Fact]
    public void Hdr_in_the_nfo()
    {
        Assert.True(new Movie { Hdr = "HDR10", VideoFile = "m.mkv" }.IsHdr);
        Assert.False(new Movie { Hdr = "", VideoFile = "m.mkv" }.IsHdr);

        const string nfo = "<movie><title>T</title><fileinfo><streamdetails><video><width>3840</width><hdrtype>{0}</hdrtype></video></streamdetails></fileinfo></movie>";
        Assert.Equal("HDR10", NfoReader.Parse(string.Format(nfo, "HDR10")).VideoHdr);
        Assert.True(string.IsNullOrEmpty(NfoReader.Parse(string.Format(nfo, "")).VideoHdr));
    }
}

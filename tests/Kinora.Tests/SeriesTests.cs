using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

public class EpisodeNameParserTests
{
    [Theory]
    [InlineData("Breaking Bad - S01E01 - Pilot Bluray-1080p.mkv", null, 1, 1, null, "Pilot")]
    [InlineData("Lost - S06E17-E18 - The End Bluray-1080p.mp4", null, 6, 17, 18, "The End")]
    [InlineData("show.s02e05e06.720p.mkv", null, 2, 5, 6, null)]
    [InlineData("Tremors.-.S01E01.-.Feeding.Frenzy.-.SD.DVD.avi", null, 1, 1, null, "Feeding Frenzy")]
    [InlineData("Scrubs 1x01.avi", 1, 1, 1, null, null)]
    [InlineData("Scrubs 3x12-13.avi", 3, 3, 12, 13, null)]
    [InlineData("05 - The Fifth One.mkv", 2, 2, 5, null, "The Fifth One")]
    [InlineData("Episode 7.mkv", 4, 4, 7, null, null)]
    [InlineData("Battlestar Galactica The Miniseries (1).mkv", null, null, null, null, "Battlestar Galactica The Miniseries (1)")]
    public void Episodes(string file, int? folderSeason, int? season, int? number, int? end, string? title)
    {
        var p = EpisodeNameParser.Parse(file, folderSeason);
        Assert.Equal(season, p.Season);
        Assert.Equal(number, p.Number);
        Assert.Equal(end, p.NumberEnd);
        if (title is not null)
            Assert.Equal(title, p.Title);
    }

    [Theory]
    [InlineData("Season 01", 1)]
    [InlineData("Season 1", 1)]
    [InlineData("season 12", 12)]
    [InlineData("Specials", 0)]
    [InlineData("Season 00", 0)]
    [InlineData("S02", 2)]
    [InlineData("Battlestar Galactica S3", 3)]
    [InlineData("Scrubs Season 4 [1080p AI x265 10bit Joy]", 4)]
    [InlineData("Stagione 2", 2)]
    [InlineData("Battlestar Galactica Miniseries", null)]
    public void Season_folders(string name, int? season) => Assert.Equal(season, EpisodeNameParser.SeasonOfFolder(name));
}

[Collection("TmdbImages")]
public sealed class SeriesScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-tv-" + Guid.NewGuid().ToString("N"));
    private readonly string _media;
    private readonly LibraryDatabase _db;
    private readonly ImageCache _posters;
    private readonly SeriesScanner _scanner;
    private readonly LibraryRoot _root;

    public SeriesScannerTests()
    {
        _media = Path.Combine(_dir, "tv");
        Directory.CreateDirectory(_media);
        _db = new LibraryDatabase(Path.Combine(_dir, "test.db"));
        _posters = new ImageCache(Path.Combine(_dir, "posters"), 320);
        _scanner = new SeriesScanner(_db, _posters);
        _root = _db.AddRoot(LibraryKind.Series, "TV", _media);
        TmdbImages.Override = FakeTmdb.Image;
    }

    public void Dispose()
    {
        TmdbImages.Override = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string File(string relative, string content = "x")
    {
        var path = Path.Combine(_media, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private Task<ScanResult> Scan(bool full = false) => _scanner.ScanAsync(_root, full, null, CancellationToken.None);

    private void BreakingBad(bool withNfo = true)
    {
        if (withNfo)
            File("Breaking Bad/tvshow.nfo", """
                <tvshow><title>Breaking Bad</title><year>2008</year><imdb_id>tt0903747</imdb_id><tmdbid>1396</tmdbid>
                <tvdbid>81189</tvdbid><status>Ended</status><genre>Drama</genre><plot>Chemistry.</plot>
                <actor><name>Bryan Cranston</name><role>Walter White</role></actor></tvshow>
                """);
        File("Breaking Bad/season01-poster.jpg");
        File("Breaking Bad/Season 01/Breaking Bad - S01E01 - Pilot Bluray-1080p.mkv");
        File("Breaking Bad/Season 01/Breaking Bad - S01E01 - Pilot Bluray-1080p.nfo",
             "<episodedetails><title>Pilot</title><season>1</season><episode>1</episode><aired>2008-01-20</aired><plot>It starts.</plot></episodedetails>");
        File("Breaking Bad/Season 01/Breaking Bad - S01E01 - Pilot Bluray-1080p-thumb.jpg");
        File("Breaking Bad/Season 01/Breaking Bad - S01E01 - Pilot Bluray-1080p.eng.srt");
        File("Breaking Bad/Season 01/Breaking Bad - S01E02 - Cat's in the Bag Bluray-1080p.mkv");
        File("Breaking Bad/Season 02/Breaking Bad - S02E01-E02 - Two in One.mkv");
        File("Breaking Bad/Specials/Breaking Bad - S00E01 - Minisode.mkv");
    }

    [Fact]
    public async Task Reads_shows_seasons_and_episodes()
    {
        BreakingBad();
        File("Tremors/Tremors.-.S01E01.-.Feeding.Frenzy.-.SD.DVD.avi");   // no season folders

        var result = await Scan();
        Assert.Equal(2, result.Added);

        var bb = _db.GetSeries().Single(s => s.Title == "Breaking Bad");
        Assert.Equal("81189", bb.TvdbId);
        Assert.Equal("tt0903747", bb.ImdbId);
        Assert.Equal("Ended", bb.Status);
        Assert.Equal(4, bb.EpisodeCount);
        Assert.Equal("Walter White", Assert.Single(_db.GetSeriesCast(bb.Id)).Role);

        var episodes = _db.GetEpisodes(bb.Id);
        var pilot = episodes.Single(e => e.Season == 1 && e.Number == 1);
        Assert.Equal("Pilot", pilot.Title);
        Assert.Equal("It starts.", pilot.Plot);
        Assert.Equal("2008-01-20", pilot.Aired);
        Assert.Equal("en", pilot.Subtitles);
        Assert.EndsWith("-thumb.jpg", pilot.ThumbFile);
        Assert.Equal("Cat's in the Bag", episodes.Single(e => e.Number == 2 && e.Season == 1).Title);
        var two = episodes.Single(e => e.Season == 2);
        Assert.Equal((1, 2), (two.Number, two.NumberEnd));
        Assert.Contains(episodes, e => e.Season == 0);

        var seasons = _db.GetSeasons(bb.Id);
        Assert.Equal([0, 1, 2], seasons.Select(s => s.Number));
        Assert.Equal("Breaking Bad/season01-poster.jpg", seasons.Single(s => s.Number == 1).PosterFile);

        var tremors = _db.GetSeries().Single(s => s.Folder == "Tremors");
        Assert.Equal("Feeding Frenzy", Assert.Single(_db.GetEpisodes(tremors.Id)).Title);
    }

    [Fact]
    public async Task Unchanged_shows_are_skipped_and_new_episodes_found()
    {
        BreakingBad();
        await Scan();
        var again = await Scan();
        Assert.Equal(1, again.Unchanged);

        File("Breaking Bad/Season 02/Breaking Bad - S02E03 - New.mkv");
        var after = await Scan();
        Assert.Equal(1, after.Updated);
        Assert.Equal(5, _db.GetSeries().Single().EpisodeCount);
    }

    [Fact]
    public async Task Watched_episodes_stay_watched_even_after_disappearing()
    {
        BreakingBad();
        await Scan();
        var bb = _db.GetSeries().Single();
        var pilot = _db.GetEpisodes(bb.Id).Single(e => e.Season == 1 && e.Number == 1);
        _db.SetEpisodesWatched(bb, [pilot], true);

        await Scan(full: true);
        Assert.True(_db.GetEpisodes(bb.Id).Single(e => e.Id == pilot.Id).Watched);

        // Renamed (a better copy): same show ids, same S01E01
        var folder = Path.Combine(_media, "Breaking Bad", "Season 01");
        System.IO.File.Move(Path.Combine(folder, "Breaking Bad - S01E01 - Pilot Bluray-1080p.mkv"), Path.Combine(folder, "Breaking Bad - S01E01 - Pilot WEB-720p.mkv"));
        await Scan();
        var renamed = _db.GetEpisodes(bb.Id).Single(e => e.VideoFile.Contains("WEB-720p"));
        Assert.True(renamed.Watched);
        Assert.Equal(1, _db.GetSeries().Single().WatchedCount);
    }

    [Fact]
    public async Task A_show_folder_that_disappears_is_removed()
    {
        BreakingBad();
        File("Tremors/Tremors.-.S01E01.-.Feeding.Frenzy.avi");
        await Scan();
        Directory.Delete(Path.Combine(_media, "Tremors"), true);
        var result = await Scan();
        Assert.Equal(1, result.Removed);
        Assert.Single(_db.GetSeries());
    }

    [Fact]
    public async Task Tmdb_fills_episodes_seasons_and_the_show()
    {
        BreakingBad(withNfo: false);
        await Scan();
        var show = _db.GetSeries().Single();
        Assert.True(show.NeedsMatch);

        var fake = new FakeTmdb();
        using var client = new TmdbClient("0123456789abcdef0123456789abcdef", "en-US", fake);
        var fetcher = new SeriesFetcher(_db, _scanner, _posters);
        var candidates = fetcher.Candidates(_root, _db.GetSeries(), retryNotFound: false);
        var result = await fetcher.FetchAsync(_root, candidates, client, writeToFolders: false, refresh: false, null, CancellationToken.None);
        Assert.Equal(1, result.Identified);

        show = _db.GetSeries().Single();
        Assert.Equal("1396", show.TmdbId);
        Assert.Equal("81189", show.TvdbId);
        Assert.Equal("A teacher turns to crime.", show.Plot);
        Assert.Equal(2013, show.EndYear);
        Assert.Equal("AMC", show.Studios);
        Assert.NotNull(show.ThumbKey);

        var episodes = _db.GetEpisodes(show.Id);
        var pilot = episodes.Single(e => e.Season == 1 && e.Number == 1);
        Assert.Equal("Pilot", pilot.Title);                 // the nfo says so
        Assert.Equal("It starts.", pilot.Plot);
        var second = episodes.Single(e => e.Season == 1 && e.Number == 2);
        Assert.Equal("Episode 1.2", second.Title);          // the file name only guessed
        Assert.Equal("Plot 1.2", second.Plot);
        Assert.Equal("/still12.jpg", second.OnlineStill);
        Assert.Equal("Episode 2.1 / Episode 2.2", episodes.Single(e => e.Season == 2).Title);

        var seasons = _db.GetSeasons(show.Id);
        Assert.Null(seasons.Single(s => s.Number == 1).OnlinePoster);   // the folder has one
        Assert.Equal("/s2.jpg", seasons.Single(s => s.Number == 2).OnlinePoster);

        // Survives a full scan, and nothing more is asked
        await Scan(full: true);
        Assert.Equal("Plot 1.2", _db.GetEpisodes(show.Id).Single(e => e.Season == 1 && e.Number == 2).Plot);
        Assert.Empty(fetcher.Candidates(_root, _db.GetSeries(), retryNotFound: false));
    }
}

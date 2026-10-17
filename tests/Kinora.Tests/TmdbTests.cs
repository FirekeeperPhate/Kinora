using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

public class TmdbMatcherTests
{
    private static TmdbSearchResult R(string id, string title, int? year, double popularity = 10, string? original = null) =>
        new(id, title, original, year, null, null, popularity);

    [Fact]
    public void Same_title_and_year_is_sure()
    {
        var (best, sure) = TmdbMatcher.Choose("Heat", 1995, [R("1103", "Heat", 1986), R("949", "Heat", 1995)]);
        Assert.True(sure);
        Assert.Equal("949", best!.Id);
    }

    [Fact]
    public void A_year_off_by_one_is_accepted_more_is_not()
    {
        Assert.True(TmdbMatcher.Choose("Heat", 1996, [R("949", "Heat", 1995)]).Confident);
        Assert.False(TmdbMatcher.Choose("Heat", 1998, [R("949", "Heat", 1995)]).Confident);
    }

    [Fact]
    public void Punctuation_articles_and_original_titles()
    {
        Assert.True(TmdbMatcher.Choose("300 - Rise of an Empire", 2014, [R("1", "300: Rise of an Empire", 2014)]).Confident);
        Assert.True(TmdbMatcher.Choose("Matrix", 1999, [R("2", "The Matrix", 1999)]).Confident);
        Assert.True(TmdbMatcher.Choose("Il buono il brutto il cattivo", 1966,
            [R("3", "The Good, the Bad and the Ugly", 1966, original: "Il buono, il brutto, il cattivo")]).Confident);
    }

    [Fact]
    public void Other_titles_or_remakes_without_year_are_left_to_the_user()
    {
        Assert.False(TmdbMatcher.Choose("Heat", 1995, [R("1", "Heatwave", 1995)]).Confident);
        Assert.False(TmdbMatcher.Choose("Heat", null, [R("949", "Heat", 1995, 40), R("1103", "Heat", 1986, 30)]).Confident);
        Assert.True(TmdbMatcher.Choose("Heat", null, [R("949", "Heat", 1995, 40), R("1103", "Heat", 1986, 3)]).Confident);
        Assert.False(TmdbMatcher.Choose("Heat", 1995, []).Confident);
    }
}

public class TmdbClientTests
{
    [Fact]
    public async Task Reads_the_details_it_needs()
    {
        var fake = new FakeTmdb();
        using var client = new TmdbClient("0123456789abcdef0123456789abcdef", "en-US", fake);
        var movie = await client.GetMovieAsync("949", CancellationToken.None);

        Assert.NotNull(movie);
        Assert.Equal("Heat", movie.Title);
        Assert.Equal("tt0113277", movie.ImdbId);
        Assert.Equal(1995, movie.Year);
        Assert.Equal(7.9, movie.VoteAverage);
        Assert.Equal("R", movie.Certification);
        Assert.Equal(["Michael Mann"], movie.Directors);
        Assert.Equal(["Crime", "Drama"], movie.Genres);
        Assert.Equal("/l949.png", movie.LogoPath);   // SVG logos are skipped
        Assert.Equal(2, movie.Cast.Count);
        Assert.Contains("api_key=0123456789abcdef0123456789abcdef", fake.Requests[0]);
    }

    [Fact]
    public async Task A_long_token_goes_in_the_header_not_in_the_url()
    {
        var fake = new FakeTmdb();
        using var client = new TmdbClient(new string('x', 200), "en-US", fake);
        Assert.True(await client.TestAsync(CancellationToken.None));
        Assert.DoesNotContain("api_key", fake.Requests[0]);
    }

    [Fact]
    public async Task A_refused_key_is_told_apart()
    {
        using var client = new TmdbClient("bad", "en-US", new FakeTmdb { RefuseKey = true });
        Assert.False(await client.TestAsync(CancellationToken.None));
    }

    [Fact]
    public void The_nfo_written_from_tmdb_reads_back()
    {
        var t = new TmdbMovie
        {
            Id = "949", ImdbId = "tt0113277", Title = "Heat", ReleaseDate = "1995-12-15", Overview = "Plot & more", Runtime = 170,
            VoteAverage = 7.9, Genres = ["Crime"], Directors = ["Michael Mann"], Collection = "Mann",
            Cast = [new TmdbCast("Al Pacino", "Hanna")],
        };
        var nfo = NfoReader.Parse(NfoWriter.Write(t));
        Assert.Equal("Heat", nfo.Title);
        Assert.Equal(1995, nfo.Year);
        Assert.Equal("Plot & more", nfo.Plot);
        Assert.Equal("tt0113277", nfo.ImdbId);
        Assert.Equal("949", nfo.TmdbId);
        Assert.Equal(7.9, nfo.Rating);
        Assert.Equal("Mann", nfo.Collection);
        Assert.Equal("Hanna", Assert.Single(nfo.Actors).Role);
    }
}

[Collection("TmdbImages")]
public sealed class MetadataFetcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-fetch-" + Guid.NewGuid().ToString("N"));
    private readonly string _media;
    private readonly LibraryDatabase _db;
    private readonly ImageCache _posters;
    private readonly MovieScanner _scanner;
    private readonly MetadataFetcher _fetcher;
    private readonly LibraryRoot _root;
    private readonly FakeTmdb _fake = new();
    private readonly TmdbClient _client;

    public MetadataFetcherTests()
    {
        _media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(_media);
        _db = new LibraryDatabase(Path.Combine(_dir, "test.db"));
        _posters = new ImageCache(Path.Combine(_dir, "posters"), 320);
        _scanner = new MovieScanner(_db, _posters);
        _fetcher = new MetadataFetcher(_db, _scanner, _posters);
        _root = _db.AddRoot(LibraryKind.Movies, "Movies", _media);
        _client = new TmdbClient("0123456789abcdef0123456789abcdef", "en-US", _fake);
        TmdbImages.Override = FakeTmdb.Image;
    }

    public void Dispose()
    {
        TmdbImages.Override = null;
        _client.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Folder(string name, string video, string? nfo = null)
    {
        var folder = Path.Combine(_media, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, video), new byte[100]);
        if (nfo is not null)
            File.WriteAllText(Path.Combine(folder, "movie.nfo"), nfo);
        return folder;
    }

    private async Task<FetchResult> Fetch(bool write = false, bool retry = false)
    {
        var movies = _db.GetMovies();
        var withCast = _db.GetCastNames().Keys.ToHashSet();
        var candidates = _fetcher.Candidates(_root, movies, withCast, retry);
        return await _fetcher.FetchAsync(_root, candidates, _client, write, refresh: false, null, CancellationToken.None);
    }

    private Movie Get(string folder) => _db.GetMovies().Single(m => m.Folder == folder);

    [Fact]
    public async Task A_movie_without_nfo_is_found_completed_and_stays_so_after_a_full_scan()
    {
        Folder("Heat (1995)", "Heat.mkv");
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);
        Assert.True(Get("Heat (1995)").NeedsMatch);

        var result = await Fetch();
        Assert.Equal(1, result.Identified);

        var heat = Get("Heat (1995)");
        Assert.Equal("949", heat.TmdbId);
        Assert.Equal("tt0113277", heat.ImdbId);
        Assert.Equal("The plot of Heat.", heat.Plot);
        Assert.Equal("Crime|Drama", heat.Genres);
        Assert.Equal("/b949.jpg", heat.OnlineFanart);
        Assert.NotNull(heat.ThumbKey);
        Assert.True(_posters.Has(heat.ThumbKey!));
        Assert.Equal(2, _db.GetCast(heat.Id).Count);

        // A scan rebuilds the movie from the folder: what came from TMDB must still be there
        await _scanner.ScanAsync(_root, true, null, CancellationToken.None);
        heat = Get("Heat (1995)");
        Assert.Equal("The plot of Heat.", heat.Plot);
        Assert.NotNull(heat.ThumbKey);
        Assert.Equal(2, _db.GetCast(heat.Id).Count);

        // Nothing more to ask
        var requests = _fake.Requests.Count;
        await Fetch();
        Assert.Equal(requests, _fake.Requests.Count);
    }

    [Fact]
    public async Task The_folder_wins_over_tmdb()
    {
        Folder("Heat (1995)", "Heat.mkv", "<movie><title>Heat (Director's cut)</title><imdbid>tt0113277</imdbid><plot>My plot</plot></movie>");
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);
        await Fetch();

        var heat = Get("Heat (1995)");
        Assert.Equal("Heat (Director's cut)", heat.Title);
        Assert.Equal("My plot", heat.Plot);
        Assert.Equal("Crime|Drama", heat.Genres);   // the gap is filled
        Assert.Equal("949", heat.TmdbId);           // found through the IMDb id
    }

    [Fact]
    public async Task A_complete_folder_is_not_even_asked_about()
    {
        Folder("Heat (1995)", "Heat.mkv", """
            <movie><title>Heat</title><year>1995</year><imdbid>tt0113277</imdbid><plot>P</plot><genre>Crime</genre>
            <actor><name>Al Pacino</name></actor></movie>
            """);
        File.WriteAllBytes(Path.Combine(_media, "Heat (1995)", "poster.jpg"), (await FakeTmdb.Image("x.jpg", default))!);
        File.WriteAllBytes(Path.Combine(_media, "Heat (1995)", "fanart.jpg"), (await FakeTmdb.Image("x.jpg", default))!);
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);

        await Fetch();
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task Unsure_matches_wait_for_the_user_and_are_not_searched_again()
    {
        Folder("Heat", "Heat.mkv");   // no year: two movies called Heat, equally known
        _fake.Movies[1] = _fake.Movies[1] with { Popularity = 30 };
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);

        var result = await Fetch();
        Assert.Equal(1, result.NotFound);
        Assert.True(Get("Heat").NeedsMatch);

        var requests = _fake.Requests.Count;
        await Fetch();
        Assert.Equal(requests, _fake.Requests.Count);
        await Fetch(retry: true);
        Assert.True(_fake.Requests.Count > requests);

        // The user says which one
        await _fetcher.IdentifyAsync(_root, Get("Heat"), "1103", _client, writeToFolders: false, CancellationToken.None);
        var heat = Get("Heat");
        Assert.Equal("1103", heat.TmdbId);
        Assert.Equal(1986, heat.Year);
        Assert.Equal("Heat", heat.Title);
    }

    [Fact]
    public async Task With_the_option_on_missing_files_are_written_and_existing_ones_kept()
    {
        var folder = Folder("Alien (1979)", "Alien.mkv");
        File.WriteAllText(Path.Combine(folder, "fanart.jpg"), "mine");   // not a real picture, but it's there
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);

        var result = await Fetch(write: true);

        Assert.Equal(3, result.FilesWritten);   // poster, logo, movie.nfo
        Assert.True(File.Exists(Path.Combine(folder, "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "logo.png")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(folder, "fanart.jpg")));
        Assert.Contains("<uniqueid type=\"tmdb\"", File.ReadAllText(Path.Combine(folder, "movie.nfo")));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));

        // The rebuild reads them as the folder's own
        var alien = Get("Alien (1979)");
        Assert.Equal("Alien (1979)/poster.jpg", alien.PosterFile);
        Assert.Equal("348", alien.TmdbId);
    }

    [Fact]
    public async Task Without_the_option_nothing_is_written()
    {
        var folder = Folder("Alien (1979)", "Alien.mkv");
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);
        await Fetch(write: false);
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task A_refused_key_stops_everything()
    {
        Folder("Heat (1995)", "Heat.mkv");
        Folder("Alien (1979)", "Alien.mkv");
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);
        _fake.RefuseKey = true;

        var result = await Fetch();
        Assert.NotNull(result.Error);
        Assert.False(result.Cancelled);
        Assert.All(_db.GetMovies(), m => Assert.True(m.NeedsMatch));
    }
}

[CollectionDefinition("TmdbImages", DisableParallelization = true)]
public class TmdbImagesCollection;

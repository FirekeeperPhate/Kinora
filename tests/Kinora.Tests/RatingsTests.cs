using System.IO.Compression;
using System.Net;
using System.Text;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

public sealed class RatingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _media;
    private readonly LibraryDatabase _db;
    private readonly MovieScanner _scanner;
    private readonly LibraryRoot _root;

    public RatingsTests()
    {
        _media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(_media);
        _db = new LibraryDatabase(Path.Combine(_dir, "test.db"));
        _scanner = new MovieScanner(_db, new ImageCache(Path.Combine(_dir, "posters"), 320));
        _root = _db.AddRoot(LibraryKind.Movies, "Movies", _media);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string KodiNfo = """
        <movie>
          <title>Heat</title>
          <ratings>
            <rating name="imdb" max="10" default="true"><value>8.3</value><votes>742,917</votes></rating>
            <rating name="themoviedb" max="10"><value>7.9</value><votes>7230</votes></rating>
            <rating name="trakt" max="10"><value>8.1</value></rating>
          </ratings>
          <userrating>9</userrating>
          <uniqueid type="imdb" default="true">tt0113277</uniqueid>
        </movie>
        """;

    [Fact]
    public void Every_named_rating_and_the_users_own_are_read_from_the_nfo()
    {
        var nfo = NfoReader.Parse(KodiNfo);
        Assert.Equal(8.3, nfo.Rating);
        Assert.Equal(8.3, nfo.ImdbRating);
        Assert.Equal(742917, nfo.ImdbVotes);
        Assert.Equal(7.9, nfo.TmdbRating);
        Assert.Equal(9, nfo.UserRating);

        var plain = NfoReader.Parse("<movie><title>Up</title><rating>7.2</rating><userrating>0</userrating></movie>");
        Assert.Equal(7.2, plain.Rating);   // a rating without a name stays the default one
        Assert.Null(plain.ImdbRating);
        Assert.Null(plain.TmdbRating);
        Assert.Null(plain.UserRating);     // Kodi writes 0 for "not rated"

        var other = NfoReader.Parse("""<tvshow><ratings><rating name="tmdb"><value>8.0</value></rating><rating name="imdb"><value>0</value></rating></ratings></tvshow>""");
        Assert.Equal(8.0, other.TmdbRating);
        Assert.Null(other.ImdbRating);
    }

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    private const string RatingsFile = "tconst\taverageRating\tnumVotes\ntt0000001\t5.7\t2170\ntt0113277\t8.3\t743002\ntt0133093\t8.7\t2213456\n";

    [Fact]
    public void Only_the_titles_asked_for_are_kept_from_imdbs_file()
    {
        var found = ImdbRatings.Read(new MemoryStream(Gzip(RatingsFile)), new HashSet<string>(["tt0113277", "tt9999999"]));
        Assert.Equal((8.3, 743002), Assert.Single(found).Value);
        Assert.Throws<InvalidDataException>(() => ImdbRatings.Read(new MemoryStream(Gzip("<html>Not found</html>")), ["tt0113277"]));
    }

    [Fact]
    public async Task Imdbs_rating_wins_over_the_nfos_and_survives_a_scan()
    {
        var folder = Path.Combine(_media, "Heat (1995)");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Heat.mkv"), new byte[1024]);
        File.WriteAllText(Path.Combine(folder, "movie.nfo"), KodiNfo);
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);

        var movie = Assert.Single(_db.GetMovies());
        Assert.Equal(8.3, movie.Imdb());
        Assert.Equal(742917, movie.ImdbVoteCount());
        Assert.Equal(7.9, movie.By(RatingSource.Tmdb));
        Assert.Equal(["tt0113277"], _db.GetLibraryImdbIds());

        Assert.Equal(1, _db.ReplaceImdbRatings(new Dictionary<string, (double, int)> { ["tt0113277"] = (8.4, 750000) }));
        Assert.Equal(0, _db.ReplaceImdbRatings(new Dictionary<string, (double, int)> { ["tt0113277"] = (8.4, 750000) }));
        await _scanner.ScanAsync(_root, true, null, CancellationToken.None);   // rebuilds the movie from the nfo
        movie = Assert.Single(_db.GetMovies());
        Assert.Equal(8.4, movie.Imdb());
        Assert.Equal(750000, movie.ImdbVoteCount());
        Assert.Equal(8.4, movie.By(RatingSource.Imdb));
        Assert.Equal(8.3, movie.By(RatingSource.Default));   // the nfo's default is still the nfo's
        Assert.Equal(8.4, _db.GetMovie(movie.Id)!.ImdbLatestRating);
    }

    [Fact]
    public async Task The_file_is_downloaded_again_only_when_old_and_read_again_for_new_titles()
    {
        File.WriteAllBytes(Path.Combine(_media, "Heat (1995) [imdbid-tt0113277].mkv"), new byte[1024]);
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);

        var server = new FakeImdb(Gzip(RatingsFile));
        var path = Path.Combine(_dir, "cache", "imdb", "title.ratings.tsv.gz");
        var imdb = new ImdbRatings(_db, path, server);
        Assert.Equal(1, await imdb.RefreshAsync(CancellationToken.None));
        Assert.Equal(1, server.Requests);
        Assert.Equal(8.3, Assert.Single(_db.GetMovies()).ImdbLatestRating);

        Assert.Equal(0, await imdb.RefreshAsync(CancellationToken.None));   // nothing new: not even read
        Assert.Equal(1, server.Requests);

        Assert.Equal(1, imdb.Clear());   // turned off: back to the nfo
        Assert.Null(Assert.Single(_db.GetMovies()).ImdbLatestRating);
        Assert.Equal(1, await imdb.RefreshAsync(CancellationToken.None));   // on again: read again, not downloaded
        Assert.Equal(1, server.Requests);

        File.WriteAllBytes(Path.Combine(_media, "The Matrix (1999) [imdbid-tt0133093].mkv"), new byte[1024]);
        await _scanner.ScanAsync(_root, false, null, CancellationToken.None);
        Assert.Equal(1, await imdb.RefreshAsync(CancellationToken.None));   // a new title: read again, not downloaded
        Assert.Equal(1, server.Requests);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - ImdbRatings.MaxAge - TimeSpan.FromHours(1));
        server.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(0, await imdb.RefreshAsync(CancellationToken.None));   // offline: the old file is still used
        Assert.Equal(2, server.Requests);
        Assert.Equal(2, _db.GetImdbRatings().Count);
    }

    private sealed class FakeImdb(byte[] file) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal(ImdbRatings.Url, request.RequestUri!.ToString());
            return Task.FromResult(Status == HttpStatusCode.OK
                ? new HttpResponseMessage(Status) { Content = new ByteArrayContent(file) }
                : new HttpResponseMessage(Status));
        }
    }
}

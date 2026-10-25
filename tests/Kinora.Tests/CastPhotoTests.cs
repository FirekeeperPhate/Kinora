using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

/// <summary>The actors' photos on a page: where they come from, in which order, and once only.</summary>
[Collection("TmdbImages")]
public sealed class CastPhotoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-cast-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly FileCache _people;
    private readonly CastPhotos _photos;
    private readonly List<string> _downloads = [];

    public CastPhotoTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "t.db"));
        _people = new FileCache(Path.Combine(_dir, "people"), ".jpg");
        _photos = new CastPhotos(_db, _people);
        TmdbImages.Override = (url, _) => { _downloads.Add(url); return Task.FromResult<byte[]?>([1, 2, 3]); };
        CastPhotos.DownloadOverride = (url, _) => { _downloads.Add(url); return Task.FromResult<byte[]?>([4, 5, 6]); };
    }

    public void Dispose()
    {
        TmdbImages.Override = null;
        CastPhotos.DownloadOverride = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static TmdbClient Client(FakeTmdb fake) => new("0123456789abcdef0123456789abcdef", "en-US", fake);

    [Fact]
    public async Task The_credits_of_a_title_are_asked_once()
    {
        var fake = new FakeTmdb();
        using var client = Client(fake);
        var first = await _photos.CreditsAsync(client, series: false, "949", TestContext.Current.CancellationToken);
        var again = await _photos.CreditsAsync(client, series: false, "949", TestContext.Current.CancellationToken);

        Assert.Equal(3, first.Count);
        Assert.Equal("/pacino.jpg", first[0].ProfilePath);
        Assert.Equal(first, again);
        Assert.Single(fake.Requests, r => r.Contains("/credits"));
        // Without a key the ones already asked are still there
        Assert.Equal(3, (await _photos.CreditsAsync(null, false, "949", TestContext.Current.CancellationToken)).Count);
        Assert.Empty(await _photos.CreditsAsync(null, false, "1103", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_show_gets_the_cast_of_all_its_seasons()
    {
        using var client = Client(new FakeTmdb());
        var cast = await _photos.CreditsAsync(client, series: true, "1396", TestContext.Current.CancellationToken);
        Assert.Equal(("Bryan Cranston", "Walter White", "17419", "/cranston.jpg"), (cast[0].Name, cast[0].Character, cast[0].Id, cast[0].ProfilePath));
    }

    [Theory]
    [InlineData("Al Pacino", null, "1158")]          // by name: Jellyfin's nfo files have no TMDB id
    [InlineData("AL PACINO", null, "1158")]
    [InlineData("Robert De Niro", "380", "380")]
    [InlineData("Somebody Else", "380", "380")]      // the id wins over the name
    [InlineData("Somebody Else", null, null)]
    public void An_actor_is_found_in_the_credits(string name, string? tmdbId, string? found)
    {
        var credits = new List<TmdbCast> { new("Al Pacino", "Hanna", "1158", "/pacino.jpg"), new("Robert De Niro", "McCauley", "380", "/deniro.jpg") };
        Assert.Equal(found, CastPhotos.Match(new CastMember { Name = name, TmdbId = tmdbId }, credits)?.Id);
    }

    [Fact]
    public async Task The_photo_comes_from_the_nfo_first_then_kodi_then_tmdb()
    {
        var folder = Path.Combine(_dir, "Heat (1995)");
        Directory.CreateDirectory(Path.Combine(folder, ".actors"));
        File.WriteAllBytes(Path.Combine(folder, ".actors", "Val_Kilmer.jpg"), [7]);
        File.WriteAllBytes(Path.Combine(folder, "pacino.jpg"), [8]);
        var tmdb = new TmdbCast("X", null, "1", "/x.jpg");
        var ct = TestContext.Current.CancellationToken;

        // A file the nfo names, next to the movie
        Assert.Equal(Path.Combine(folder, "pacino.jpg"), Plain(await _photos.PhotoAsync(new CastMember { Name = "Al Pacino", Thumb = "pacino.jpg" }, tmdb, folder, ct)));
        // A web address in the nfo: downloaded once, then from the cache
        var web = new CastMember { Name = "Ashley Judd", Thumb = "https://example.org/judd.jpg" };
        var file = await _photos.PhotoAsync(web, tmdb, folder, ct);
        Assert.Equal([4, 5, 6], File.ReadAllBytes(file!));
        Assert.Equal(file, await _photos.PhotoAsync(web, tmdb, folder, ct));
        Assert.Single(_downloads, d => d.Contains("judd"));
        // Kodi's .actors folder
        Assert.Equal(Path.Combine(folder, ".actors", "Val_Kilmer.jpg"), Plain(await _photos.PhotoAsync(new CastMember { Name = "Val Kilmer" }, tmdb, folder, ct)));
        // A Jellyfin path on another computer: skipped, TMDB's photo instead
        var jellyfin = new CastMember { Name = "Tom Sizemore", Thumb = @"C:\ProgramData\Jellyfin-not-here\People\T\Tom Sizemore\folder.jpg" };
        Assert.Equal([1, 2, 3], File.ReadAllBytes((await _photos.PhotoAsync(jellyfin, tmdb, folder, ct))!));
        Assert.Contains(_downloads, d => d.Contains("w185/x.jpg"));
        // Nothing anywhere
        Assert.Null(await _photos.PhotoAsync(new CastMember { Name = "Nobody" }, null, folder, ct));
        Assert.Null(await _photos.PhotoAsync(new CastMember { Name = "Nobody" }, new TmdbCast("Nobody", null, "2"), null, ct));
    }

    [Fact]
    public void The_nfo_photo_is_read()
    {
        var path = Path.Combine(_dir, "movie.nfo");
        File.WriteAllText(path, """
            <movie><title>Heat</title>
              <actor><name>Al Pacino</name><role>Hanna</role><thumb>https://image.tmdb.org/t/p/original/pacino.jpg</thumb></actor>
              <actor><name>Val Kilmer</name></actor>
            </movie>
            """);
        var nfo = NfoReader.Read(path)!;
        Assert.Equal("https://image.tmdb.org/t/p/original/pacino.jpg", nfo.Actors[0].Thumb);
        Assert.Null(nfo.Actors[1].Thumb);
    }

    private static string? Plain(string? path) => path is null ? null : Kinora.Core.ExactPath.Display(path);
}

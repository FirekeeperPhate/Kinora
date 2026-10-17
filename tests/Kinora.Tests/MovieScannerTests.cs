using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Scanning;
using SkiaSharp;

namespace Kinora.Tests;

public sealed class MovieScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _media;
    private readonly LibraryDatabase _db;
    private readonly ImageCache _posters;
    private readonly MovieScanner _scanner;
    private readonly LibraryRoot _root;

    public MovieScannerTests()
    {
        _media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(_media);
        _db = new LibraryDatabase(Path.Combine(_dir, "test.db"));
        _posters = new ImageCache(Path.Combine(_dir, "posters"), 320);
        _scanner = new MovieScanner(_db, _posters);
        _root = _db.AddRoot(LibraryKind.Movies, "Movies", _media);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string MovieFolder(string name, string video, string? nfo = null, bool poster = false)
    {
        var folder = Path.Combine(_media, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, video), new byte[1024]);
        if (nfo is not null)
            File.WriteAllText(Path.Combine(folder, "movie.nfo"), nfo);
        if (poster)
            WriteJpeg(Path.Combine(folder, "poster.jpg"), 600, 900);
        return folder;
    }

    private static void WriteJpeg(string path, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(SKColors.SteelBlue);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 80);
        File.WriteAllBytes(path, data.ToArray());
    }

    private Task<ScanResult> Scan(bool full = false) => _scanner.ScanAsync(_root, full, null, CancellationToken.None);

    [Fact]
    public async Task Finds_movies_reads_nfo_and_makes_thumbnails()
    {
        MovieFolder("Heat (1995)", "Heat.mkv",
            "<movie><title>Heat</title><year>1995</year><imdbid>tt0113277</imdbid><genre>Crime</genre><actor><name>Al Pacino</name></actor></movie>",
            poster: true);
        MovieFolder("Alien (1979)", "Alien.mp4");
        File.WriteAllBytes(Path.Combine(_media, "Loose.Movie.2010.1080p.mkv"), new byte[10]);

        var result = await Scan();

        Assert.True(result.RootAvailable);
        Assert.Equal(3, result.Added);
        var movies = _db.GetMovies().ToDictionary(m => m.Title);

        var heat = movies["Heat"];
        Assert.Equal("Heat (1995)", heat.Folder);
        Assert.Equal("Heat (1995)/Heat.mkv", heat.VideoFile);
        Assert.Equal("Crime", heat.Genres);
        Assert.Equal("Heat (1995)/poster.jpg", heat.PosterFile);
        Assert.NotNull(heat.ThumbKey);
        Assert.True(_posters.Has(heat.ThumbKey!));
        using (var thumb = SKBitmap.Decode(_posters.PathFor(heat.ThumbKey!)))
            Assert.Equal(320, thumb.Width);
        Assert.Equal("Al Pacino", Assert.Single(_db.GetCast(heat.Id)).Name);

        var alien = movies["Alien"];
        Assert.Equal(1979, alien.Year);
        Assert.True(alien.NeedsMatch);

        var loose = movies["Loose Movie"];
        Assert.Equal(2010, loose.Year);
        Assert.Equal("", loose.Folder);
    }

    [Fact]
    public async Task A_second_scan_skips_unchanged_folders_and_removes_missing_ones()
    {
        MovieFolder("Heat (1995)", "Heat.mkv");
        var alien = MovieFolder("Alien (1979)", "Alien.mp4");
        await Scan();

        var again = await Scan();
        Assert.Equal(2, again.Unchanged);
        Assert.Equal(0, again.Added + again.Updated + again.Removed);

        Directory.Delete(alien, true);
        var afterDelete = await Scan();
        Assert.Equal(1, afterDelete.Removed);
        Assert.Equal("Heat", Assert.Single(_db.GetMovies()).Title);
    }

    [Fact]
    public async Task Watched_survives_a_rescan_and_a_disappearance()
    {
        var heat = MovieFolder("Heat (1995)", "Heat.mkv", "<movie><title>Heat</title><imdbid>tt0113277</imdbid></movie>");
        await Scan();
        _db.SetWatched(_db.GetMovies().Single(), true);

        await Scan(full: true);
        Assert.True(_db.GetMovies().Single().Watched);

        // Moved outside Kinora: gone from one place, back in another, same IMDb id
        var moved = Path.Combine(_media, "Heat");
        Directory.Move(heat, moved);
        await Scan();
        var back = _db.GetMovies().Single();
        Assert.Equal("Heat/Heat.mkv", back.VideoFile);
        Assert.True(back.Watched);
    }

    [Fact]
    public async Task Collection_folders_extras_and_versions()
    {
        var collection = Path.Combine(_media, "Alien Collection");
        Directory.CreateDirectory(collection);
        Directory.CreateDirectory(Path.Combine(collection, "Alien (1979)"));
        File.WriteAllBytes(Path.Combine(collection, "Alien (1979)", "Alien.mkv"), new byte[100]);
        File.WriteAllBytes(Path.Combine(collection, "Alien (1979)", "Alien-trailer.mkv"), new byte[500]);
        Directory.CreateDirectory(Path.Combine(collection, "Aliens (1986)"));
        File.WriteAllBytes(Path.Combine(collection, "Aliens (1986)", "Aliens.mkv"), new byte[100]);
        File.WriteAllBytes(Path.Combine(collection, "Aliens (1986)", "Aliens Special Edition.mkv"), new byte[200]);

        await Scan();

        var movies = _db.GetMovies().ToDictionary(m => m.Title);
        Assert.Equal(2, movies.Count);
        Assert.Equal("Alien Collection/Alien (1979)/Alien.mkv", movies["Alien"].VideoFile);
        Assert.Equal("", movies["Alien"].OtherVideos);
        Assert.Equal("Alien Collection/Aliens (1986)/Aliens Special Edition.mkv", movies["Aliens"].VideoFile);
        Assert.Equal("Alien Collection/Aliens (1986)/Aliens.mkv", movies["Aliens"].OtherVideos);
    }

    [Fact]
    public async Task An_unreachable_root_deletes_nothing()
    {
        MovieFolder("Heat (1995)", "Heat.mkv");
        await Scan();

        _root.Path = Path.Combine(_dir, "unplugged");
        var result = await Scan();

        Assert.False(result.RootAvailable);
        Assert.Single(_db.GetMovies());
    }
}

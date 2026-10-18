using Kinora.Core;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Moving;
using Kinora.Core.Scanning;
using SkiaSharp;

namespace Kinora.Tests;

/// <summary>
/// Names ending with a dot ("S.W.A.T."): Windows would read them as the name without it, which
/// may be another folder next to it. Every test puts the two side by side.
/// </summary>
[Collection("Moving")]
public sealed class ExactPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-exact-" + Guid.NewGuid().ToString("N"));
    private readonly string _movieDir;
    private readonly string _otherDir;
    private readonly string _tvDir;
    private readonly LibraryDatabase _db;
    private readonly MovieScanner _movies;
    private readonly SeriesScanner _series;
    private readonly LibraryMover _mover;
    private readonly LibraryRoot _movieRoot;
    private readonly LibraryRoot _otherRoot;
    private readonly LibraryRoot _tvRoot;

    public ExactPathTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows drops the final dot");
        _movieDir = Path.Combine(_dir, "Movies");
        _otherDir = Path.Combine(_dir, "Other");
        _tvDir = Path.Combine(_dir, "TV");
        foreach (var d in new[] { _movieDir, _otherDir, _tvDir })
            Directory.CreateDirectory(d);
        _db = new LibraryDatabase(Path.Combine(_dir, "test.db"));
        var posters = new ImageCache(Path.Combine(_dir, "posters"), 320);
        _movies = new MovieScanner(_db, posters);
        _series = new SeriesScanner(_db, posters);
        _mover = new LibraryMover(_db, _movies, _series);
        _movieRoot = _db.AddRoot(LibraryKind.Movies, "Movies", _movieDir);
        _otherRoot = _db.AddRoot(LibraryKind.Movies, "Other", _otherDir);
        _tvRoot = _db.AddRoot(LibraryKind.Series, "TV", _tvDir);
    }

    public void Dispose()
    {
        FolderMover.ForceCopy = false;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Io(_dir), true); } catch (IOException) { }
    }

    private static string Io(string path) => @"\\?\" + path;

    /// <summary>Writes through "\\?\", so a name ending with a dot is created as it is.</summary>
    private static void Write(string path, int size = 100)
    {
        Directory.CreateDirectory(Io(Path.GetDirectoryName(path)!));
        File.WriteAllBytes(Io(path), new byte[size]);
    }

    private static void WriteJpeg(string path)
    {
        Directory.CreateDirectory(Io(Path.GetDirectoryName(path)!));
        using var bmp = new SKBitmap(300, 450);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(SKColors.SteelBlue);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 80);
        File.WriteAllBytes(Io(path), data.ToArray());
    }

    private void Twins()
    {
        Write(Path.Combine(_tvDir, "S.W.A.T.", "Season 01", "SWAT 2017 S01E01.mkv"));
        Write(Path.Combine(_tvDir, "S.W.A.T.", "Season 01", "SWAT 2017 S01E02.mkv"));
        Write(Path.Combine(_tvDir, "S.W.A.T", "Season 01", "SWAT 1975 S01E01.mkv"));
    }

    [Fact]
    public async Task Two_shows_named_alike_each_have_their_own_episodes()
    {
        Twins();
        await _series.ScanAsync(_tvRoot, true, null, TestContext.Current.CancellationToken);

        var shows = _db.GetSeries(_tvRoot.Id).ToDictionary(s => s.Folder);
        Assert.Equal(["S.W.A.T", "S.W.A.T."], shows.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["S.W.A.T./Season 01/SWAT 2017 S01E01.mkv", "S.W.A.T./Season 01/SWAT 2017 S01E02.mkv"],
                     _db.GetEpisodes(shows["S.W.A.T."].Id).Select(e => e.VideoFile).Order(StringComparer.Ordinal));
        Assert.Equal(["S.W.A.T/Season 01/SWAT 1975 S01E01.mkv"], _db.GetEpisodes(shows["S.W.A.T"].Id).Select(e => e.VideoFile));
    }

    [Fact]
    public async Task A_show_read_again_keeps_its_own_episodes()
    {
        Twins();
        await _series.ScanAsync(_tvRoot, true, null, TestContext.Current.CancellationToken);
        var dotted = _db.GetSeries(_tvRoot.Id).Single(s => s.Folder == "S.W.A.T.");
        Write(Path.Combine(_tvDir, "S.W.A.T.", "Season 01", "SWAT 2017 S01E03.mkv"));

        await _series.RebuildAsync(_tvRoot, [dotted], TestContext.Current.CancellationToken);
        Assert.Equal(3, _db.GetEpisodes(dotted.Id).Count);
    }

    [Fact]
    public async Task Two_movies_named_alike_each_have_their_own_video_and_poster()
    {
        Write(Path.Combine(_movieDir, "Heat.", "Heat 1995.mkv"));
        WriteJpeg(Path.Combine(_movieDir, "Heat.", "poster.jpg"));
        Write(Path.Combine(_movieDir, "Heat", "Heat 1986.mkv"));
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);

        var movies = _db.GetMovies(_movieRoot.Id).ToDictionary(m => m.Folder);
        Assert.Equal("Heat./Heat 1995.mkv", movies["Heat."].VideoFile);
        Assert.Equal("Heat/Heat 1986.mkv", movies["Heat"].VideoFile);
        Assert.NotNull(movies["Heat."].ThumbKey);
        Assert.Null(movies["Heat"].ThumbKey);
    }

    [Theory]
    [InlineData(false)]   // another share or disk: copied, checked, then deleted
    [InlineData(true)]    // the same disk: renamed
    public async Task A_movie_named_like_another_moves_without_touching_it(bool sameDisk)
    {
        FolderMover.ForceCopy = !sameDisk;
        Write(Path.Combine(_movieDir, "Heat.", "Heat 1995.mkv"), 5000);
        Write(Path.Combine(_movieDir, "Heat.", "Heat 1995.en.srt"));
        Write(Path.Combine(_movieDir, "Heat", "Heat 1986.mkv"));
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);
        var dotted = _db.GetMovies(_movieRoot.Id).Single(m => m.Folder == "Heat.");

        Assert.Equal(5100, LibraryMover.SizeOf(dotted, _movieRoot));
        var result = await _mover.MoveMovieAsync(dotted, _movieRoot, _otherRoot, null, TestContext.Current.CancellationToken);

        Assert.Equal(MoveOutcome.Moved, result.Outcome);
        Assert.False(Directory.Exists(Io(Path.Combine(_movieDir, "Heat."))));
        Assert.True(File.Exists(Io(Path.Combine(_otherDir, "Heat.", "Heat 1995.mkv"))));
        Assert.True(File.Exists(Io(Path.Combine(_otherDir, "Heat.", "Heat 1995.en.srt"))));
        Assert.True(File.Exists(Path.Combine(_movieDir, "Heat", "Heat 1986.mkv")));
        Assert.False(Directory.Exists(Path.Combine(_otherDir, "Heat")));
        var moved = _db.GetMovie(dotted.Id)!;
        Assert.Equal((_otherRoot.Id, "Heat./Heat 1995.mkv"), (moved.RootId, moved.VideoFile));
    }

    [Theory]
    [InlineData(@"H:\S.W.A.T.", @"\\?\H:\S.W.A.T.")]
    [InlineData(@"H:\", @"\\?\H:\")]
    [InlineData(@"\\nas\video\S.W.A.T.", @"\\?\UNC\nas\video\S.W.A.T.")]
    [InlineData(@"H:/Movies/Heat.", @"\\?\H:\Movies\Heat.")]
    [InlineData(@"\\?\H:\x", @"\\?\H:\x")]
    [InlineData(@"relative\x", @"relative\x")]
    public void Paths_for_the_file_functions(string path, string io)
    {
        Assert.Equal(io, ExactPath.ForIo(path));
        if (Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            Assert.Equal(path.Replace('/', '\\'), ExactPath.Display(io));
    }

    [Fact]
    public void A_picture_keeps_its_key_whichever_way_it_is_reached()
    {
        var when = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(ImageCache.KeyFor(@"H:\Heat (1995)\poster.jpg", 10, when), ImageCache.KeyFor(@"\\?\H:\Heat (1995)\poster.jpg", 10, when));
        Assert.Equal(ImageCache.KeyFor(@"\\nas\video\x.jpg", 10, when), ImageCache.KeyFor(@"\\?\UNC\nas\video\x.jpg", 10, when));
    }
}

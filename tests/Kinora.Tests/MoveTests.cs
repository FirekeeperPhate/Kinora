using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Moving;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

[Collection("Moving")]
public sealed class MoveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-move-" + Guid.NewGuid().ToString("N"));
    private readonly string _a;
    private readonly string _b;
    private readonly LibraryDatabase _db;
    private readonly MovieScanner _movies;
    private readonly SeriesScanner _series;
    private readonly LibraryMover _mover;
    private readonly LibraryRoot _rootA;
    private readonly LibraryRoot _rootB;

    public MoveTests()
    {
        _a = Path.Combine(_dir, "ToWatch");
        _b = Path.Combine(_dir, "Movies");
        Directory.CreateDirectory(_a);
        Directory.CreateDirectory(_b);
        _db = new LibraryDatabase(Path.Combine(_dir, "test.db"));
        var posters = new ImageCache(Path.Combine(_dir, "posters"), 320);
        _movies = new MovieScanner(_db, posters);
        _series = new SeriesScanner(_db, posters);
        _mover = new LibraryMover(_db, _movies, _series);
        _rootA = _db.AddRoot(LibraryKind.Movies, "ToWatch", _a);
        _rootB = _db.AddRoot(LibraryKind.Movies, "Movies", _b);
    }

    public void Dispose()
    {
        FolderMover.ForceCopy = false;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Write(string path, int size = 1000)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    private async Task<Movie> Heat()
    {
        var folder = Path.Combine(_a, "Heat (1995)");
        Write(Path.Combine(folder, "Heat.mkv"), 300_000);
        Write(Path.Combine(folder, "Heat.en.srt"));
        Write(Path.Combine(folder, "Extras", "Heat-trailer.mkv"));
        File.WriteAllText(Path.Combine(folder, "movie.nfo"), "<movie><title>Heat</title><imdbid>tt0113277</imdbid></movie>");
        Write(Path.Combine(folder, "Desktop.ini"), 10);
        File.SetAttributes(Path.Combine(folder, "Desktop.ini"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        await _movies.ScanAsync(_rootA, false, null, default);
        var movie = _db.GetMovies(_rootA.Id).Single();
        _db.SetWatched(movie, true);
        _db.SaveMatch(_rootA.Id, movie.VideoFile, "949", manual: true);
        return _db.GetMovie(movie.Id)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_movie_folder_moves_whole_and_keeps_its_row(bool copy)
    {
        FolderMover.ForceCopy = copy;
        var movie = await Heat();

        var result = await _mover.MoveMovieAsync(movie, _rootA, _rootB, null, default);

        Assert.Equal(MoveOutcome.Moved, result.Outcome);
        Assert.Equal(!copy, result.Renamed);
        Assert.False(Directory.Exists(Path.Combine(_a, "Heat (1995)")));
        var moved = Path.Combine(_b, "Heat (1995)");
        Assert.Equal(300_000, new FileInfo(Path.Combine(moved, "Heat.mkv")).Length);
        Assert.True(File.Exists(Path.Combine(moved, "Extras", "Heat-trailer.mkv")));
        Assert.True(File.GetAttributes(Path.Combine(moved, "Desktop.ini")).HasFlag(FileAttributes.ReadOnly) || !copy);
        Assert.Empty(Directory.GetDirectories(_b, "*" + FolderMover.WorkSuffix));

        var row = _db.GetMovie(movie.Id)!;
        Assert.Equal(_rootB.Id, row.RootId);
        Assert.Equal("Heat (1995)/Heat.mkv", row.VideoFile);
        Assert.True(row.Watched);
        Assert.Equal("en", row.Subtitles);
        Assert.Equal("949", _db.GetMatch(_rootB.Id, row.VideoFile)?.TmdbId);
        Assert.Null(_db.GetMatch(_rootA.Id, movie.VideoFile));

        // Scans of both roots agree: nothing added, nothing removed
        var a = await _movies.ScanAsync(_rootA, false, null, default);
        var b = await _movies.ScanAsync(_rootB, false, null, default);
        Assert.Equal(0, a.Added + a.Removed + b.Added + b.Removed);
        Assert.Single(_db.GetMovies());
    }

    [Fact]
    public async Task A_name_already_there_stops_everything()
    {
        var movie = await Heat();
        Directory.CreateDirectory(Path.Combine(_b, "Heat (1995)"));

        var result = await _mover.MoveMovieAsync(movie, _rootA, _rootB, null, default);

        Assert.Equal(MoveOutcome.Conflict, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_a, "Heat (1995)", "Heat.mkv")));
        Assert.Equal(_rootA.Id, _db.GetMovie(movie.Id)!.RootId);
    }

    [Fact]
    public async Task Cancelled_halfway_the_source_is_untouched_and_the_copy_gone()
    {
        FolderMover.ForceCopy = true;
        var movie = await Heat();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<MoveProgress>(p => { if (p.BytesDone > 0) cts.Cancel(); });

        var result = await _mover.MoveMovieAsync(movie, _rootA, _rootB, progress, cts.Token);

        Assert.Equal(MoveOutcome.Cancelled, result.Outcome);
        Assert.Equal(5, Directory.GetFiles(Path.Combine(_a, "Heat (1995)"), "*", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFileSystemEntries(_b));
        Assert.Equal(_rootA.Id, _db.GetMovie(movie.Id)!.RootId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_loose_movie_moves_with_the_files_named_like_it(bool copy)
    {
        FolderMover.ForceCopy = copy;
        Write(Path.Combine(_a, "Heat.mkv"), 5000);
        Write(Path.Combine(_a, "Heat.nfo"));
        Write(Path.Combine(_a, "Heat-poster.jpg"));
        Write(Path.Combine(_a, "Heat.it.srt"));
        Write(Path.Combine(_a, "Heat 2.mkv"));   // another movie
        await _movies.ScanAsync(_rootA, false, null, default);
        var heat = _db.GetMovies(_rootA.Id).Single(m => m.VideoFile == "Heat.mkv");

        var result = await _mover.MoveMovieAsync(heat, _rootA, _rootB, null, default);

        Assert.Equal(MoveOutcome.Moved, result.Outcome);
        Assert.Equal(["Heat 2.mkv"], Directory.GetFiles(_a).Select(Path.GetFileName));
        Assert.Equal(4, Directory.GetFiles(_b).Length);
        Assert.Equal("Heat.mkv", _db.GetMovie(heat.Id)!.VideoFile);
        Assert.Equal(_rootB.Id, _db.GetMovie(heat.Id)!.RootId);
    }

    [Fact]
    public async Task A_movie_inside_a_collection_folder_lands_at_the_top()
    {
        Write(Path.Combine(_a, "Alien Collection", "Alien (1979)", "Alien.mkv"));
        Write(Path.Combine(_a, "Alien Collection", "Alien (1979)", "poster.jpg"), 10);
        await _movies.ScanAsync(_rootA, false, null, default);
        var alien = _db.GetMovies(_rootA.Id).Single();

        await _mover.MoveMovieAsync(alien, _rootA, _rootB, null, default);

        var row = _db.GetMovie(alien.Id)!;
        Assert.Equal("Alien (1979)", row.Folder);
        Assert.Equal("Alien (1979)/Alien.mkv", row.VideoFile);
        Assert.True(Directory.Exists(Path.Combine(_a, "Alien Collection")));   // the collection folder stays
    }

    [Theory]
    [InlineData("Alien Collection/Alien (1979)/Alien.mkv", "Alien Collection/Alien (1979)", "Alien (1979)", "Alien (1979)/Alien.mkv")]
    [InlineData("Heat.mkv", "", "", "Heat.mkv")]
    [InlineData("Other/x.mkv", "Heat", "Heat", "Other/x.mkv")]
    public void Reprefix(string rel, string oldPrefix, string newPrefix, string expected) =>
        Assert.Equal(expected, LibraryDatabase.Reprefix(rel, oldPrefix, newPrefix));

    /// <summary>Progress&lt;T&gt; posts to a context; tests need the callback at once.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

[CollectionDefinition("Moving", DisableParallelization = true)]
public class MovingCollection;

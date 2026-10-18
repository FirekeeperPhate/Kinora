using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Moving;
using Kinora.Core.Scanning;

namespace Kinora.Tests;

public sealed class DeleteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-delete-" + Guid.NewGuid().ToString("N"));
    private readonly string _movieDir;
    private readonly string _tvDir;
    private readonly LibraryDatabase _db;
    private readonly MovieScanner _movies;
    private readonly SeriesScanner _series;
    private readonly LibraryDeleter _deleter;
    private readonly LibraryRoot _movieRoot;
    private readonly LibraryRoot _tvRoot;

    public DeleteTests()
    {
        _movieDir = Path.Combine(_dir, "Movies");
        _tvDir = Path.Combine(_dir, "TV");
        Directory.CreateDirectory(_movieDir);
        Directory.CreateDirectory(_tvDir);
        _db = new LibraryDatabase(Path.Combine(_dir, "test.db"));
        var posters = new ImageCache(Path.Combine(_dir, "posters"), 320);
        _movies = new MovieScanner(_db, posters);
        _series = new SeriesScanner(_db, posters);
        _deleter = new LibraryDeleter(_db, _movies, _series);
        _movieRoot = _db.AddRoot(LibraryKind.Movies, "Movies", _movieDir);
        _tvRoot = _db.AddRoot(LibraryKind.Series, "TV", _tvDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void Write(string path, int size = 100)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    private Movie MovieNamed(string title) => _db.GetMovies(_movieRoot.Id).Single(m => m.Title == title);

    [Fact]
    public async Task A_movie_goes_with_its_whole_folder()
    {
        var heat = Path.Combine(_movieDir, "Heat (1995)");
        Write(Path.Combine(heat, "Heat.mkv"), 5000);
        Write(Path.Combine(heat, "Heat.en.srt"));
        Write(Path.Combine(heat, "Extras", "Heat-trailer.mkv"));
        Write(Path.Combine(heat, "Desktop.ini"));
        File.SetAttributes(Path.Combine(heat, "Desktop.ini"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        Write(Path.Combine(_movieDir, "Alien (1979)", "Alien.mkv"));
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);

        var plan = _deleter.PlanMovie(MovieNamed("Heat"), _movieRoot);
        Assert.True(plan.CanDelete);
        Assert.Equal(heat, plan.Folder);
        Assert.Equal(4, plan.FileCount);
        Assert.Empty(plan.UnknownVideos!);   // the trailer in Extras belongs to the movie

        var result = await _deleter.DeleteMovieAsync(MovieNamed("Heat"), _movieRoot);
        Assert.True(result.Deleted, result.Error);
        Assert.False(Directory.Exists(heat));
        Assert.True(File.Exists(Path.Combine(_movieDir, "Alien (1979)", "Alien.mkv")));
        Assert.True(Directory.Exists(_movieDir));
        Assert.Equal(["Alien"], _db.GetMovies(_movieRoot.Id).Select(m => m.Title));
    }

    [Fact]
    public async Task A_loose_movie_takes_only_its_own_files()
    {
        foreach (var f in new[] { "Heat.mkv", "Heat.nfo", "Heat.en.srt", "Heat-poster.jpg", "Heat 2.mkv", "Heat 2.nfo", "Heat-2.nfo" })
            Write(Path.Combine(_movieDir, f));
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);
        var heat = _db.GetMovies(_movieRoot.Id).Single(m => m.VideoFile == "Heat.mkv");

        var result = await _deleter.DeleteMovieAsync(heat, _movieRoot);
        Assert.True(result.Deleted, result.Error);
        Assert.Equal(["Heat 2.mkv", "Heat 2.nfo", "Heat-2.nfo"],
                     Directory.EnumerateFiles(_movieDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(_db.GetMovies(_movieRoot.Id), m => m.Id == heat.Id);
    }

    [Fact]
    public async Task Videos_the_library_doesnt_know_are_named_before_deleting()
    {
        // A movie folder inside another one isn't read by the scanner: deleting Alien would take Aliens too
        Write(Path.Combine(_movieDir, "Alien (1979)", "Alien.mkv"));
        Write(Path.Combine(_movieDir, "Alien (1979)", "Aliens (1986)", "Aliens.mkv"));
        Write(Path.Combine(_movieDir, "Alien (1979)", "Featurettes", "Making of.mkv"));
        Write(Path.Combine(_movieDir, "Alien (1979)", "Alien-trailer.mkv"));
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);

        var plan = _deleter.PlanMovie(MovieNamed("Alien"), _movieRoot);
        Assert.True(plan.CanDelete);
        Assert.Equal([Path.Combine("Aliens (1986)", "Aliens.mkv")], plan.UnknownVideos);
    }

    [Fact]
    public async Task A_folder_with_other_titles_of_the_library_inside_is_not_deleted()
    {
        Write(Path.Combine(_tvDir, "Star Wars", "Season 01", "Star Wars S01E01.mkv"));
        await _series.ScanAsync(_tvRoot, true, null, TestContext.Current.CancellationToken);
        var show = _db.GetSeries(_tvRoot.Id).Single();
        // Another show the library has inside this one's folder (put there by hand in the database)
        var inner = new Series { RootId = _tvRoot.Id, Folder = "Star Wars/Andor", Title = "Andor" };
        _db.SaveSeries(inner, [], [], []);

        var plan = _deleter.PlanSeries(show, _tvRoot);
        Assert.False(plan.CanDelete);
        var result = await _deleter.DeleteSeriesAsync(show, _tvRoot);
        Assert.False(result.Deleted);
        Assert.True(File.Exists(Path.Combine(_tvDir, "Star Wars", "Season 01", "Star Wars S01E01.mkv")));
        Assert.Equal(2, _db.GetSeries(_tvRoot.Id).Count);
    }

    [Theory]
    [InlineData(@"H:\Heat (1995)", @"H:\", true)]
    [InlineData(@"H:\", @"H:\", false)]
    [InlineData(@"H:\Movies\Heat", @"H:\Movies", true)]
    [InlineData(@"H:\Movies\Heat", @"H:\Movies\", true)]
    [InlineData(@"H:\Movies", @"H:\Movies", false)]
    [InlineData(@"H:\Movies\", @"H:\Movies", false)]
    [InlineData(@"H:\Movies2\Heat", @"H:\Movies", false)]
    [InlineData(@"H:\Movies\..\Heat", @"H:\Movies", false)]
    [InlineData(@"\nas\video\Heat", @"\nas\video", true)]
    public void Strictly_inside_the_library_folder(string path, string root, bool inside)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths");
        Assert.Equal(inside, LibraryDeleter.IsStrictlyInside(path, root));
    }

    [Fact]
    public async Task A_show_goes_with_its_folder()
    {
        var show = Path.Combine(_tvDir, "Andor");
        Write(Path.Combine(show, "Season 01", "Andor S01E01.mkv"));
        Write(Path.Combine(show, "Season 01", "Andor S01E02.mkv"));
        Write(Path.Combine(_tvDir, "Severance", "Season 01", "Severance S01E01.mkv"));
        await _series.ScanAsync(_tvRoot, true, null, TestContext.Current.CancellationToken);
        var andor = _db.GetSeries(_tvRoot.Id).Single(s => s.Folder == "Andor");

        var result = await _deleter.DeleteSeriesAsync(andor, _tvRoot);
        Assert.True(result.Deleted, result.Error);
        Assert.False(Directory.Exists(show));
        Assert.True(Directory.Exists(Path.Combine(_tvDir, "Severance")));
        Assert.Equal(["Severance"], _db.GetSeries(_tvRoot.Id).Select(s => s.Folder));
    }

    [Fact]
    public async Task An_episode_takes_its_files_and_nothing_of_the_others()
    {
        var season = Path.Combine(_tvDir, "Andor", "Season 01");
        foreach (var f in new[] { "Andor S01E01.mkv", "Andor S01E01.nfo", "Andor S01E01.en.srt", "Andor S01E01-thumb.jpg",
                                  "Andor S01E01.E02.mkv", "Andor S01E01.E02.nfo", "Andor S01E03.mkv" })
            Write(Path.Combine(season, f));
        await _series.ScanAsync(_tvRoot, true, null, TestContext.Current.CancellationToken);
        var andor = _db.GetSeries(_tvRoot.Id).Single();
        var episodes = _db.GetEpisodes(andor.Id);
        var first = episodes.Single(e => e.VideoFile.EndsWith("Andor S01E01.mkv", StringComparison.Ordinal));

        var result = await _deleter.DeleteEpisodeAsync(andor, first, _tvRoot);
        Assert.True(result.Deleted, result.Error);
        Assert.Equal(["Andor S01E01.E02.mkv", "Andor S01E01.E02.nfo", "Andor S01E03.mkv"],
                     Directory.EnumerateFiles(season).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var left = _db.GetEpisodes(andor.Id);
        Assert.Equal(episodes.Count - 1, left.Count);
        Assert.DoesNotContain(left, e => e.Id == first.Id);
    }

    [Fact]
    public void Nothing_outside_the_library_folder()
    {
        Write(Path.Combine(_dir, "Elsewhere", "x.mkv"));
        var movie = new Movie { Id = 1, RootId = _movieRoot.Id, Folder = "../Elsewhere", VideoFile = "../Elsewhere/x.mkv", Title = "x" };
        Assert.False(_deleter.PlanMovie(movie, _movieRoot).CanDelete);
        var show = new Series { Id = 1, RootId = _tvRoot.Id, Folder = "", Title = "x" };
        Assert.False(_deleter.PlanSeries(show, _tvRoot).CanDelete);
    }

    // ---- from the review ----

    private static string Io(string path) => OperatingSystem.IsWindows() ? @"\\?\" + path : path;

    [Fact]
    public async Task A_name_ending_with_a_dot_deletes_that_folder_not_its_twin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows drops the final dot");
        // "S.W.A.T." and "S.W.A.T" side by side: Windows would take the first for the second
        var dotted = Path.Combine(_tvDir, "S.W.A.T.");
        var plain = Path.Combine(_tvDir, "S.W.A.T");
        Directory.CreateDirectory(Io(Path.Combine(dotted, "Season 01")));
        File.WriteAllBytes(Io(Path.Combine(dotted, "Season 01", "SWAT 2017 S01E01.mkv")), new byte[100]);
        Write(Path.Combine(plain, "Season 01", "SWAT 1975 S01E01.mkv"));
        var show = new Series { RootId = _tvRoot.Id, Folder = "S.W.A.T.", Title = "S.W.A.T." };
        _db.SaveSeries(show, [], [], []);

        var plan = _deleter.PlanSeries(show, _tvRoot);
        Assert.True(plan.CanDelete, plan.Refusal);
        Assert.Equal(1, plan.FileCount);
        var result = await _deleter.DeleteSeriesAsync(show, _tvRoot);
        Assert.True(result.Deleted, result.Error);
        Assert.False(Directory.Exists(Io(dotted)));
        Assert.True(File.Exists(Path.Combine(plain, "Season 01", "SWAT 1975 S01E01.mkv")));
    }

    [Fact]
    public async Task A_folder_gone_offline_leaves_the_library_as_it_was()
    {
        Write(Path.Combine(_movieDir, "Heat (1995)", "Heat.mkv"));
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);
        var heat = MovieNamed("Heat");
        // The NAS goes to sleep between the question and the delete
        Directory.Move(_movieDir, _movieDir + "-asleep");

        var result = await _deleter.DeleteMovieAsync(heat, _movieRoot);
        Assert.False(result.Deleted);
        Assert.False(result.Partly);
        Assert.Single(_db.GetMovies(_movieRoot.Id));
        Assert.True(File.Exists(Path.Combine(_movieDir + "-asleep", "Heat (1995)", "Heat.mkv")));
    }

    [Fact]
    public async Task A_video_in_use_stops_the_delete_before_anything_goes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "An open file can't be deleted only on Windows");
        var heat = Path.Combine(_movieDir, "Heat (1995)");
        Write(Path.Combine(heat, "Heat.mkv"));
        Write(Path.Combine(heat, "movie.nfo"));
        Write(Path.Combine(heat, "poster.jpg"));
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);

        DeleteResult result;
        using (File.Open(Path.Combine(heat, "Heat.mkv"), FileMode.Open, FileAccess.Read, FileShare.None))   // playing
            result = await _deleter.DeleteMovieAsync(MovieNamed("Heat"), _movieRoot);
        Assert.False(result.Deleted);
        Assert.False(result.Partly);
        Assert.Equal(3, Directory.GetFiles(heat).Length);
        Assert.Single(_db.GetMovies(_movieRoot.Id));
    }

    [Fact]
    public async Task A_show_deleted_only_in_part_is_read_again()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "An open file can't be deleted only on Windows");
        var season = Path.Combine(_tvDir, "Andor", "Season 01");
        foreach (var e in new[] { "01", "02", "03" })
            Write(Path.Combine(season, $"Andor S01E{e}.mkv"));
        Write(Path.Combine(season, "Andor S01E03.en.srt"));
        await _series.ScanAsync(_tvRoot, true, null, TestContext.Current.CancellationToken);
        var andor = _db.GetSeries(_tvRoot.Id).Single();

        DeleteResult result;
        using (File.Open(Path.Combine(season, "Andor S01E03.en.srt"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = await _deleter.DeleteSeriesAsync(andor, _tvRoot);
        Assert.False(result.Deleted);
        Assert.True(result.Partly);
        // The videos went first; the library now says what is there: a show with no episodes
        Assert.Empty(_db.GetEpisodes(andor.Id));
    }

    [Fact]
    public async Task The_last_episode_of_a_show_can_go()
    {
        Write(Path.Combine(_tvDir, "Andor", "Season 01", "Andor S01E01.mkv"));
        await _series.ScanAsync(_tvRoot, true, null, TestContext.Current.CancellationToken);
        var andor = _db.GetSeries(_tvRoot.Id).Single();
        var only = _db.GetEpisodes(andor.Id).Single();

        var result = await _deleter.DeleteEpisodeAsync(andor, only, _tvRoot);
        Assert.True(result.Deleted, result.Error);
        Assert.Empty(_db.GetEpisodes(andor.Id));
    }

    [Fact]
    public async Task A_folder_with_another_library_folder_inside_is_not_deleted()
    {
        // A series library folder set up inside the movie one: "TV" scans as a movie of the movie library
        var tv = Path.Combine(_movieDir, "TV");
        Write(Path.Combine(tv, "Andor S01E01.mkv"));
        _db.AddRoot(LibraryKind.Series, "TV inside", tv);
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);
        var movie = _db.GetMovies(_movieRoot.Id).Single(m => m.Folder == "TV");

        var plan = _deleter.PlanMovie(movie, _movieRoot);
        Assert.False(plan.CanDelete);
        Assert.Contains("library folder", plan.Refusal);
        Assert.True(File.Exists(Path.Combine(tv, "Andor S01E01.mkv")));
    }

    [Fact]
    public async Task A_big_file_next_to_a_loose_movie_is_another_video()
    {
        Write(Path.Combine(_movieDir, "Heat.avi"));
        Write(Path.Combine(_movieDir, "Heat.nfo"));
        Write(Path.Combine(_movieDir, "Heat.rmvb"), 21 * 1024 * 1024);   // an extension Kinora doesn't know
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);

        var result = await _deleter.DeleteMovieAsync(_db.GetMovies(_movieRoot.Id).Single(), _movieRoot);
        Assert.True(result.Deleted, result.Error);
        Assert.Equal(["Heat.rmvb"], Directory.EnumerateFiles(_movieDir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_junction_in_the_folder_is_not_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing");
        var outside = Path.Combine(_dir, "Outside");
        Write(Path.Combine(outside, "keep.mkv"));
        File.SetAttributes(Path.Combine(outside, "keep.mkv"), FileAttributes.ReadOnly);
        var heat = Path.Combine(_movieDir, "Heat (1995)");
        Write(Path.Combine(heat, "Heat.mkv"));
        var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(heat, "link")}\" \"{outside}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        await link.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.SkipUnless(Directory.Exists(Path.Combine(heat, "link")), "mklink /J failed");
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);

        var plan = _deleter.PlanMovie(MovieNamed("Heat"), _movieRoot);
        Assert.Equal(1, plan.FileCount);
        Assert.Empty(plan.UnknownVideos!);
        var result = await _deleter.DeleteMovieAsync(MovieNamed("Heat"), _movieRoot);
        Assert.True(result.Deleted, result.Error);
        Assert.False(Directory.Exists(heat));
        Assert.True(File.Exists(Path.Combine(outside, "keep.mkv")));
        Assert.True(File.GetAttributes(Path.Combine(outside, "keep.mkv")).HasFlag(FileAttributes.ReadOnly));
    }

    [Theory]
    [InlineData("Alien Collection/Alien (1979)", true)]
    [InlineData("S.W.A.T.", true)]
    [InlineData("", false)]
    [InlineData("../Elsewhere", false)]
    [InlineData("a/../b", false)]
    [InlineData("a//b", false)]
    [InlineData("./a", false)]
    [InlineData("C:/x", false)]
    [InlineData("/x", false)]
    public void Relative_paths_that_stay_inside(string rel, bool safe) => Assert.Equal(safe, LibraryDeleter.IsSafeRelative(rel));

    [Fact]
    public async Task A_title_inside_another_library_folder_is_not_deleted()
    {
        // The series library sits inside the movie one; a season folder scans as a movie there
        var tv = Path.Combine(_movieDir, "TV");
        Write(Path.Combine(tv, "Andor", "Season 01", "Andor S01E01.mkv"));
        _db.AddRoot(LibraryKind.Series, "TV inside", tv);
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);
        var movie = _db.GetMovies(_movieRoot.Id).Single();
        Assert.StartsWith("TV/", movie.Folder);

        var plan = _deleter.PlanMovie(movie, _movieRoot);
        Assert.False(plan.CanDelete);
        Assert.Contains("TV inside", plan.Refusal);
    }

    [Fact]
    public async Task A_title_folder_that_is_a_junction_is_not_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing");
        var target = Path.Combine(_dir, "Elsewhere", "Heat (1995)");
        Write(Path.Combine(target, "Heat.mkv"));
        Write(Path.Combine(target, "movie.nfo"));
        var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_movieDir, "Heat (1995)")}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        await link.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.SkipUnless(Directory.Exists(Path.Combine(_movieDir, "Heat (1995)")), "mklink /J failed");
        await _movies.ScanAsync(_movieRoot, true, null, TestContext.Current.CancellationToken);

        var result = await _deleter.DeleteMovieAsync(MovieNamed("Heat"), _movieRoot);
        Assert.False(result.Deleted);
        Assert.Contains("link", result.Error);
        Assert.Equal(2, Directory.GetFiles(target).Length);
        Assert.Single(_db.GetMovies(_movieRoot.Id));
        Directory.Delete(Path.Combine(_movieDir, "Heat (1995)"));   // the link, for the cleanup
    }
}

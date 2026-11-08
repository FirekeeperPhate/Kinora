using Kinora.Core;
using Kinora.Core.Data;
using Kinora.Core.MediaServers;
using Kinora.Core.Models;
using Kinora.Core.Playback;

namespace Kinora.Tests;

/// <summary>What was watched and when, and the player's clear voices.</summary>
public sealed class HistoryTests : IDisposable
{
    private const long Hour = 3_600_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-history-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly LibraryRoot _root;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public HistoryTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "t.db"));
        _root = _db.AddRoot(LibraryKind.Movies, "Movies", "M:/Movies");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Movie AddMovie(string file, string title, bool watched = false)
    {
        var movie = new Movie { RootId = _root.Id, VideoFile = file, Folder = Path.GetDirectoryName(file) ?? "", Title = title, Year = 2000, Watched = watched };
        _db.SaveMovies([(movie, [])]);
        return _db.GetMovies().Single(m => m.Id == movie.Id);
    }

    private static long At(int year, int month, int day) => new DateTimeOffset(year, month, day, 21, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void Viewings_are_kept_most_recent_first_and_summed_by_year()
    {
        var heat = AddMovie("Heat/Heat.mkv", "Heat");
        _db.AddHistory(PlaybackKind.Movie, heat.Id, null, "Heat", 2 * Hour, At(2025, 12, 30));
        _db.AddHistory(PlaybackKind.Movie, heat.Id, null, "Heat", 2 * Hour, At(2026, 2, 1));
        _db.AddHistory(PlaybackKind.Episode, 7, 3, "S01E01 · Pilot", Hour / 2, At(2026, 3, 1));

        var history = _db.GetHistory();
        Assert.Equal(["S01E01 · Pilot", "Heat", "Heat"], history.Select(h => h.Title));

        var movies = WatchHistory.Summarize(history, PlaybackKind.Movie, new DateTime(2026, 11, 8));
        Assert.Equal(new WatchSummary(1, 2, 2, 4), movies);
        var episodes = WatchHistory.Summarize(history, PlaybackKind.Episode, new DateTime(2026, 11, 8));
        Assert.Equal(1, episodes.ThisYear);
        Assert.Equal(0.5, episodes.HoursThisYear);
    }

    [Fact]
    public void An_unmark_takes_back_only_the_viewing_of_the_last_day()
    {
        var heat = AddMovie("Heat/Heat.mkv", "Heat");
        _db.AddHistory(PlaybackKind.Movie, heat.Id, null, "Heat", 0, At(2025, 1, 1));
        _db.AddHistory(PlaybackKind.Movie, heat.Id, null, "Heat", 0);   // just now, by mistake

        _db.RemoveRecentHistory(PlaybackKind.Movie, heat.Id);
        _db.RemoveRecentHistory(PlaybackKind.Movie, heat.Id);   // nothing recent left: the old one stays

        Assert.Single(_db.GetHistory());
    }

    [Fact]
    public void A_title_that_leaves_the_library_leaves_its_viewings_without_its_id()
    {
        var heat = AddMovie("Heat/Heat.mkv", "Heat");
        _db.AddHistory(PlaybackKind.Movie, heat.Id, null, "Heat", 2 * Hour);
        _db.DeleteMovies([heat.Id]);

        var entry = Assert.Single(_db.GetHistory());
        Assert.Null(entry.ItemId);          // the next movie to get this id isn't the one watched
        Assert.Equal("Heat", entry.Title);  // and the viewing still counts
        var alien = AddMovie("Alien/Alien.mkv", "Alien");
        Assert.False(_db.HasHistory(PlaybackKind.Movie, alien.Id));
    }

    [Fact]
    public async Task The_first_sync_fills_the_history_with_the_server_dates()
    {
        var watchedHere = AddMovie("A/A.mkv", "A", watched: true);
        var watchedThere = AddMovie("B/B.mkv", "B");
        AddMovie("C/C.mkv", "C");
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", "/m/A/A.mkv", played: true, lastPlayed: "2026-03-01T20:00:00Z"));
        server.Items.Add(FakeMediaServer.Movie("b", "/m/B/B.mkv", played: true, lastPlayed: "2026-04-02T20:00:00Z"));
        server.Items.Add(FakeMediaServer.Movie("c", "/m/C/C.mkv"));
        using var client = new MediaServerClient("http://nas:8096", FakeMediaServer.Key, server);
        await client.ConnectAsync(_ct);
        var sync = new MediaServerSync(_db, client, "u1");

        await sync.ApplyAsync(await sync.PlanAsync(_ct), null, _ct);
        await sync.ApplyAsync(await sync.PlanAsync(_ct), null, _ct);   // again: nothing twice

        var history = _db.GetHistory();
        Assert.Equal([watchedThere.Id, watchedHere.Id], history.Select(h => h.ItemId!.Value));
        Assert.Equal(DateTimeOffset.Parse("2026-04-02T20:00:00Z").ToUnixTimeSeconds(), history[0].WatchedAt);
        Assert.Equal(2 * Hour, history[0].LengthMs);
    }

    [Fact]
    public async Task A_mark_pushed_from_here_comes_back_as_no_viewing_and_two_copies_as_one()
    {
        // Marked here with a whole season or folder, then pushed: the server dates it "now"
        var pushed = AddMovie("A/A.mkv", "A", watched: true);
        _db.SaveSyncStates([(PlaybackKind.Movie, pushed.Id, true, 0, "a")]);
        // The same movie twice here, found by its id; one viewing there
        var copy1 = AddMovie("B1/B 1080p.mkv", "B", watched: false);
        var copy2 = AddMovie("B2/B 2160p.mkv", "B", watched: false);
        foreach (var copy in new[] { copy1, copy2 })
        {
            copy.ImdbId = "tt0000099";
            _db.SaveMovies([(copy, [])]);
        }
        var server = new FakeMediaServer();
        server.Items.Add(FakeMediaServer.Movie("a", "/m/A/A.mkv", played: true, lastPlayed: "2026-11-08T07:00:00Z"));
        server.Items.Add(FakeMediaServer.Movie("b", "/elsewhere/B.mkv", name: "B", played: true, imdb: "tt0000099", lastPlayed: "2026-06-01T20:00:00Z"));
        using var client = new MediaServerClient("http://nas:8096", FakeMediaServer.Key, server);
        await client.ConnectAsync(_ct);
        var sync = new MediaServerSync(_db, client, "u1");

        await sync.ApplyAsync(await sync.PlanAsync(_ct), null, _ct);

        var entry = Assert.Single(_db.GetHistory());
        Assert.Equal("B", entry.Title);
        Assert.True(_db.GetMovies().Where(m => m.ImdbId == "tt0000099").All(m => m.Watched));   // both copies marked all the same
    }

    [Fact]
    public void Dates_say_the_year_only_when_it_is_another()
    {
        var now = new DateTime(2026, 11, 8);
        Assert.Equal("3 Nov", WatchHistory.Date(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), now));
        Assert.Equal("3 Nov 2025", WatchHistory.Date(new DateTimeOffset(2025, 11, 3, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), now));
    }

    [Fact]
    public void Clear_voices_has_a_value_for_each_of_vlcs_ten_bands()
    {
        Assert.Equal(10, PlaybackRules.ClearVoicesBands.Length);
        Assert.All(PlaybackRules.ClearVoicesBands, b => Assert.InRange(b, -20f, 20f));   // VLC's range
        Assert.True(PlaybackRules.ClearVoicesBands[0] < 0 && PlaybackRules.ClearVoicesBands[6] > 0);   // rumble down, voices up
    }
}

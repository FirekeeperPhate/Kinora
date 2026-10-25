using Kinora.Core.Data;
using Kinora.Core.Playback;
using Microsoft.Data.Sqlite;

namespace Kinora.Tests;

public sealed class PlaybackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kinora-playback-" + Guid.NewGuid().ToString("N"));

    public PlaybackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const long Minute = 60_000;

    [Theory]
    [InlineData(30_000, 120 * Minute, false, false)]   // the first half minute: nothing to keep
    [InlineData(5 * Minute, 120 * Minute, true, false)]
    [InlineData(107 * Minute, 120 * Minute, true, false)]   // just under 90%
    [InlineData(108 * Minute, 120 * Minute, false, true)]   // 90%: the credits, it counts as seen
    [InlineData(120 * Minute, 120 * Minute, false, true)]
    [InlineData(5 * Minute, 0, true, false)]   // length unknown: keep the place, never "watched"
    public void Where_a_stop_counts(long position, long length, bool resume, bool watched)
    {
        Assert.Equal(resume, PlaybackRules.WorthResuming(position, length));
        Assert.Equal(watched, PlaybackRules.CountsAsWatched(position, length));
    }

    [Fact]
    public void How_much_was_seen()
    {
        Assert.Null(PlaybackRules.SeenFraction(null));
        Assert.Null(PlaybackRules.SeenFraction(new PlaybackPosition(5 * Minute, 0)));   // length unknown
        Assert.Equal(0.5, PlaybackRules.SeenFraction(new PlaybackPosition(60 * Minute, 120 * Minute)));
        Assert.Equal(1, PlaybackRules.SeenFraction(new PlaybackPosition(130 * Minute, 120 * Minute)));   // never past the end
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65_000, "1:05")]
    [InlineData(3_733_000, "1:02:13")]
    [InlineData(-5, "0:00")]
    public void Clock(long ms, string text) => Assert.Equal(text, PlaybackRules.Clock(ms));

    [Theory]
    [InlineData("Track 1 - [English]", 1, "English")]
    [InlineData("English 440 Hz - [English]", 1, "English 440 Hz")]
    [InlineData("Director's commentary - [English]", 2, "Director's commentary (English)")]
    [InlineData("Track 3", 3, "Track 3")]
    [InlineData(null, 4, "Track 4")]
    [InlineData("  ", 2, "Track 2")]
    public void Track_names_for_the_menus(string? raw, int index, string name) =>
        Assert.Equal(name, PlaybackRules.TrackName(raw, index));

    [Fact]
    public void The_place_is_kept_per_movie_and_per_episode()
    {
        var db = new LibraryDatabase(Path.Combine(_dir, "t.db"));
        Assert.Null(db.GetPlayback(PlaybackKind.Movie, 7));

        db.SavePlayback(PlaybackKind.Movie, 7, 5 * Minute, 120 * Minute);
        db.SavePlayback(PlaybackKind.Episode, 7, 2 * Minute, 40 * Minute);   // same id, other kind
        db.SavePlayback(PlaybackKind.Movie, 7, 6 * Minute, 120 * Minute);    // later: replaces

        Assert.Equal(new PlaybackPosition(6 * Minute, 120 * Minute), db.GetPlayback(PlaybackKind.Movie, 7));
        Assert.Equal(new PlaybackPosition(2 * Minute, 40 * Minute), db.GetPlayback(PlaybackKind.Episode, 7));

        // The library's "started" marks, one kind at a time
        db.SavePlayback(PlaybackKind.Movie, 9, 30 * Minute, 120 * Minute);
        var movies = db.GetPlaybackPositions(PlaybackKind.Movie);
        Assert.Equal([7L, 9L], movies.Keys.Order());
        Assert.Equal(0.25, PlaybackRules.SeenFraction(movies[9]));
        Assert.Single(db.GetPlaybackPositions(PlaybackKind.Episode));

        db.ClearPlayback(PlaybackKind.Movie, 7);
        Assert.Null(db.GetPlayback(PlaybackKind.Movie, 7));
        Assert.NotNull(db.GetPlayback(PlaybackKind.Episode, 7));
    }

    [Fact]
    public void A_library_from_the_version_before_gets_the_table()
    {
        var path = Path.Combine(_dir, "old.db");
        _ = new LibraryDatabase(path);
        // As Kinora 0.1.3 left it: schema 4 (no playback, no tmdb_credits, no actor photos)
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE playback; DROP TABLE tmdb_credits; ALTER TABLE cast_members DROP COLUMN thumb; ALTER TABLE series_cast DROP COLUMN thumb; PRAGMA user_version = 4;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new LibraryDatabase(path);
        db.SavePlayback(PlaybackKind.Movie, 1, 3 * Minute, 90 * Minute);
        Assert.NotNull(db.GetPlayback(PlaybackKind.Movie, 1));
    }
}

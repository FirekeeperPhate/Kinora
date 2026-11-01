using Kinora.Core.Data;
using Kinora.Core.Models;
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

        Assert.Equal(6 * Minute, db.GetPlayback(PlaybackKind.Movie, 7)!.PositionMs);
        Assert.Equal(40 * Minute, db.GetPlayback(PlaybackKind.Episode, 7)!.LengthMs);

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
            cmd.CommandText = """
                DROP TABLE playback; DROP TABLE tmdb_credits; ALTER TABLE cast_members DROP COLUMN thumb; ALTER TABLE series_cast DROP COLUMN thumb;
                DROP TABLE track_choices; DROP TABLE series_played; DROP TABLE server_sync; ALTER TABLE roots DROP COLUMN watched_move_root;
                PRAGMA user_version = 4;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new LibraryDatabase(path);
        db.SavePlayback(PlaybackKind.Movie, 1, 3 * Minute, 90 * Minute);
        Assert.NotNull(db.GetPlayback(PlaybackKind.Movie, 1));
        db.SaveTrackChoice(null, subtitle: false, "Italian", "Italian");
        Assert.Equal("Italian", db.GetTrackChoices(null).Audio!.Name);
    }

    private static Episode Ep(long id, int? season, int? number, bool watched = false) =>
        new() { Id = id, Season = season, Number = number, Watched = watched, VideoFile = $"e{id}.mkv" };

    [Fact]
    public void The_order_of_the_episodes()
    {
        var special = Ep(1, 0, 1);
        var one = Ep(2, 1, 1, watched: true);
        var two = Ep(3, 1, 2);
        var nextSeason = Ep(4, 2, 1);
        Episode[] all = [nextSeason, special, two, one];

        Assert.Equal([2L, 3L, 4L, 1L], EpisodeOrder.Sorted(all).Select(e => e.Id));
        Assert.Equal(3, EpisodeOrder.FirstToWatch(all)!.Id);
        Assert.Equal(4, EpisodeOrder.After(all, two)!.Id);     // across the seasons
        Assert.Equal(2, EpisodeOrder.Before(all, two)!.Id);
        Assert.Null(EpisodeOrder.Before(all, one));
        Assert.Null(EpisodeOrder.After(all, nextSeason));      // the show doesn't run on into the specials
        Assert.Null(EpisodeOrder.Before(all, special));
        Assert.Null(EpisodeOrder.After(all, Ep(99, 1, 9)));    // not one of them any more
    }

    [Fact]
    public void Everything_seen_starts_again_and_specials_wait()
    {
        Assert.Equal(2, EpisodeOrder.FirstToWatch([Ep(2, 1, 1, watched: true), Ep(1, 0, 1)])!.Id);
        Assert.Null(EpisodeOrder.FirstToWatch([]));
    }

    [Theory]
    [InlineData("Track 1 - [English]", "English")]
    [InlineData("Commentary - [Italian]", "Italian")]
    [InlineData("Track 3", null)]
    [InlineData(null, null)]
    public void The_language_in_a_track_name(string? raw, string? language) =>
        Assert.Equal(language, PlaybackRules.TrackLanguage(raw));

    [Fact]
    public void The_track_chosen_last_time()
    {
        (int, string, string?)[] tracks = [(-1, "Off", null), (3, "English", "English"), (5, "Commentary (Italian)", "Italian"), (6, "Italiano", "Italian")];

        Assert.Equal(5, PlaybackRules.PickTrack(tracks, new TrackMemory("commentary (italian)", "Italian")));   // the very same
        Assert.Equal(5, PlaybackRules.PickTrack(tracks, new TrackMemory("Forced", "Italian")));                 // the same language
        Assert.Equal(-1, PlaybackRules.PickTrack(tracks, new TrackMemory("Off", null)));
        Assert.Null(PlaybackRules.PickTrack(tracks, new TrackMemory("Deutsch", "German")));                     // VLC's choice stays
        Assert.Null(PlaybackRules.PickTrack(tracks, new TrackMemory("Deutsch", null)));
        Assert.Null(PlaybackRules.PickTrack(tracks, null));
    }

    [Fact]
    public void Choices_are_remembered_for_the_show_and_in_general()
    {
        var db = new LibraryDatabase(Path.Combine(_dir, "t.db"));
        Assert.Equal((null, null), db.GetTrackChoices("series:1"));

        db.SaveTrackChoice("series:1", subtitle: false, "Italian", "Italian");
        db.SaveTrackChoice("series:2", subtitle: false, "English", "English");
        db.SaveTrackChoice("series:2", subtitle: true, "Off", null);

        Assert.Equal("Italian", db.GetTrackChoices("series:1").Audio!.Name);
        Assert.Equal("English", db.GetTrackChoices("series:2").Audio!.Name);
        Assert.Equal("English", db.GetTrackChoices("series:3").Audio!.Name);    // the last one, in general
        Assert.Equal("English", db.GetTrackChoices(null).Audio!.Name);
        Assert.Equal("Off", db.GetTrackChoices("series:1").Subtitle!.Name);     // what the show hasn't got comes from the general one
        Assert.Null(db.GetTrackChoices("series:1").Subtitle!.Language);

        db.SaveTrackChoice(null, subtitle: true, "French", "French");
        Assert.Equal("Off", db.GetTrackChoices("series:2").Subtitle!.Name);     // the show keeps its own
        Assert.Equal("French", db.GetTrackChoices("series:1").Subtitle!.Name);
    }
}

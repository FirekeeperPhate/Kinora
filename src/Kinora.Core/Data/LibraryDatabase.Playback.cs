using Dapper;

namespace Kinora.Core.Data;

/// <summary>What the built-in player plays: a movie or an episode, by its id.</summary>
public enum PlaybackKind
{
    Movie,
    Episode,
}

/// <param name="Updated">When it was left, Unix seconds.</param>
public sealed record PlaybackPosition(long PositionMs, long LengthMs, long Updated = 0);

/// <summary>The episode of a show that was left halfway most recently.</summary>
public sealed record StartedEpisode(long SeriesId, long EpisodeId, PlaybackPosition Position);

/// <summary>The audio or subtitle track last chosen: its name in the menu, and its language.</summary>
public sealed record TrackMemory(string Name, string? Language);

/// <summary>Where the built-in player stopped, and what was chosen in it.</summary>
public sealed partial class LibraryDatabase
{
    /// <summary>The memory that holds for everything without one of its own.</summary>
    public const string AllTitles = "all";

    private static string KindText(PlaybackKind kind) => kind == PlaybackKind.Movie ? "movie" : "episode";

    public PlaybackPosition? GetPlayback(PlaybackKind kind, long id)
    {
        using var c = Open();
        return c.Query<(long PositionMs, long LengthMs, long Updated)>(
                    "SELECT position_ms, length_ms, updated FROM playback WHERE kind = @kind AND item_id = @id",
                    new { kind = KindText(kind), id })
                .Select(r => new PlaybackPosition(r.PositionMs, r.LengthMs, r.Updated))
                .FirstOrDefault();
    }

    /// <summary>Every place kept for one kind, by item id: the "started" marks of a whole library in one read.</summary>
    public Dictionary<long, PlaybackPosition> GetPlaybackPositions(PlaybackKind kind)
    {
        using var c = Open();
        return c.Query<(long Id, long PositionMs, long LengthMs, long Updated)>(
                    "SELECT item_id, position_ms, length_ms, updated FROM playback WHERE kind = @kind", new { kind = KindText(kind) })
                .ToDictionary(r => r.Id, r => new PlaybackPosition(r.PositionMs, r.LengthMs, r.Updated));
    }

    /// <summary>For each show, the episode left halfway most recently.</summary>
    public Dictionary<long, StartedEpisode> GetStartedEpisodes()
    {
        using var c = Open();
        var result = new Dictionary<long, StartedEpisode>();
        foreach (var (series, episode, position, length, updated) in c.Query<(long, long, long, long, long)>("""
            SELECT e.series_id, p.item_id, p.position_ms, p.length_ms, p.updated
            FROM playback p JOIN episodes e ON e.id = p.item_id
            WHERE p.kind = 'episode' ORDER BY p.updated DESC
            """))
            result.TryAdd(series, new StartedEpisode(series, episode, new PlaybackPosition(position, length, updated)));
        return result;
    }

    public void SavePlayback(PlaybackKind kind, long id, long positionMs, long lengthMs, long? updated = null)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO playback (kind, item_id, position_ms, length_ms, updated) VALUES (@kind, @id, @positionMs, @lengthMs, @now)
            ON CONFLICT (kind, item_id) DO UPDATE SET position_ms = excluded.position_ms, length_ms = excluded.length_ms, updated = excluded.updated
            """, new { kind = KindText(kind), id, positionMs, lengthMs, now = updated ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public void ClearPlayback(PlaybackKind kind, long id)
    {
        using var c = Open();
        c.Execute("DELETE FROM playback WHERE kind = @kind AND item_id = @id", new { kind = KindText(kind), id });
    }

    // ---- shows in progress ----

    /// <summary>A show's episode was played: "Continue watching" goes by this.</summary>
    public void SetSeriesPlayed(long seriesId)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO series_played (series_id, updated) VALUES (@seriesId, @now)
            ON CONFLICT (series_id) DO UPDATE SET updated = excluded.updated
            """, new { seriesId, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    /// <summary>When each show was last played, Unix seconds.</summary>
    public Dictionary<long, long> GetSeriesPlayed()
    {
        using var c = Open();
        return c.Query<(long Id, long Updated)>("SELECT series_id, updated FROM series_played").ToDictionary(r => r.Id, r => r.Updated);
    }

    // ---- audio and subtitle choices ----

    /// <summary>What was chosen in a scope ("series:12", or <see cref="AllTitles"/>), falling back on the general one.</summary>
    public (TrackMemory? Audio, TrackMemory? Subtitle) GetTrackChoices(string? scope)
    {
        using var c = Open();
        var rows = c.Query<(string Scope, string? AudioName, string? AudioLanguage, string? SubtitleName, string? SubtitleLanguage)>(
            "SELECT scope, audio_name, audio_language, subtitle_name, subtitle_language FROM track_choices WHERE scope = @scope OR scope = @all",
            new { scope, all = AllTitles }).ToList();
        var own = rows.FirstOrDefault(r => r.Scope == scope);
        var general = rows.FirstOrDefault(r => r.Scope == AllTitles);
        var audio = own.AudioName ?? general.AudioName;
        var subtitle = own.SubtitleName ?? general.SubtitleName;
        return (audio is null ? null : new TrackMemory(audio, own.AudioName is not null ? own.AudioLanguage : general.AudioLanguage),
                subtitle is null ? null : new TrackMemory(subtitle, own.SubtitleName is not null ? own.SubtitleLanguage : general.SubtitleLanguage));
    }

    /// <summary>Remembers a choice for the show, and in general.</summary>
    public void SaveTrackChoice(string? scope, bool subtitle, string name, string? language)
    {
        var (nameColumn, languageColumn) = subtitle ? ("subtitle_name", "subtitle_language") : ("audio_name", "audio_language");
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var s in new[] { scope, AllTitles }.Where(s => s is not null).Distinct())
            c.Execute($"""
                INSERT INTO track_choices (scope, {nameColumn}, {languageColumn}) VALUES (@s, @name, @language)
                ON CONFLICT (scope) DO UPDATE SET {nameColumn} = excluded.{nameColumn}, {languageColumn} = excluded.{languageColumn}
                """, new { s, name, language }, tx);
        tx.Commit();
    }
}

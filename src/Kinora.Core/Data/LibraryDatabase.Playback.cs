using Dapper;

namespace Kinora.Core.Data;

/// <summary>What the built-in player plays: a movie or an episode, by its id.</summary>
public enum PlaybackKind
{
    Movie,
    Episode,
}

public sealed record PlaybackPosition(long PositionMs, long LengthMs);

/// <summary>Where the built-in player stopped, to start from there next time.</summary>
public sealed partial class LibraryDatabase
{
    private static string KindText(PlaybackKind kind) => kind == PlaybackKind.Movie ? "movie" : "episode";

    public PlaybackPosition? GetPlayback(PlaybackKind kind, long id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<PlaybackPosition>(
            "SELECT position_ms AS PositionMs, length_ms AS LengthMs FROM playback WHERE kind = @kind AND item_id = @id",
            new { kind = KindText(kind), id });
    }

    public void SavePlayback(PlaybackKind kind, long id, long positionMs, long lengthMs)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO playback (kind, item_id, position_ms, length_ms, updated) VALUES (@kind, @id, @positionMs, @lengthMs, @now)
            ON CONFLICT (kind, item_id) DO UPDATE SET position_ms = excluded.position_ms, length_ms = excluded.length_ms, updated = excluded.updated
            """, new { kind = KindText(kind), id, positionMs, lengthMs, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public void ClearPlayback(PlaybackKind kind, long id)
    {
        using var c = Open();
        c.Execute("DELETE FROM playback WHERE kind = @kind AND item_id = @id", new { kind = KindText(kind), id });
    }
}

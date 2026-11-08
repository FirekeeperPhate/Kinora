using Dapper;

namespace Kinora.Core.Data;

/// <summary>A movie or an episode watched, and when.</summary>
/// <param name="ItemId">The movie or the episode; null once it has left the library (the viewing still counts).</param>
/// <param name="Title">As it was then: "Heat", "S01E03 · Pilot".</param>
public sealed record HistoryEntry(long Id, PlaybackKind Kind, long? ItemId, long? SeriesId, string Title, long WatchedAt, long LengthMs);

/// <summary>What was watched and when: from the player, from a title marked by hand, from the media server's dates.</summary>
public sealed partial class LibraryDatabase
{
    /// <summary>An unmark this soon after is a mistake taken back, not a viewing to forget.</summary>
    private static readonly TimeSpan Regret = TimeSpan.FromDays(1);

    public void AddHistory(PlaybackKind kind, long itemId, long? seriesId, string title, long lengthMs, long? watchedAt = null)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO watch_history (kind, item_id, series_id, title, watched_at, length_ms)
            VALUES (@kind, @itemId, @seriesId, @title, @at, @lengthMs)
            """, new { kind = KindText(kind), itemId, seriesId, title, at = watchedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), lengthMs = Math.Max(0, lengthMs) });
    }

    /// <summary>Marked unwatched: the viewing recorded in the last day goes; older ones happened.</summary>
    public void RemoveRecentHistory(PlaybackKind kind, long itemId)
    {
        using var c = Open();
        c.Execute("""
            DELETE FROM watch_history WHERE id = (
                SELECT id FROM watch_history WHERE kind = @kind AND item_id = @itemId AND watched_at >= @since
                ORDER BY watched_at DESC LIMIT 1)
            """, new { kind = KindText(kind), itemId, since = DateTimeOffset.UtcNow.Subtract(Regret).ToUnixTimeSeconds() });
    }

    public bool HasHistory(PlaybackKind kind, long itemId)
    {
        using var c = Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM watch_history WHERE kind = @kind AND item_id = @itemId",
                                     new { kind = KindText(kind), itemId }) > 0;
    }

    /// <summary>Everything, most recent first.</summary>
    public List<HistoryEntry> GetHistory()
    {
        using var c = Open();
        return c.Query<(long Id, string Kind, long? ItemId, long? SeriesId, string Title, long WatchedAt, long LengthMs)>(
                    "SELECT id, kind, item_id, series_id, title, watched_at, length_ms FROM watch_history ORDER BY watched_at DESC, id DESC")
                .Select(r => new HistoryEntry(r.Id, r.Kind == "movie" ? PlaybackKind.Movie : PlaybackKind.Episode, r.ItemId, r.SeriesId, r.Title, r.WatchedAt, r.LengthMs))
                .ToList();
    }
}

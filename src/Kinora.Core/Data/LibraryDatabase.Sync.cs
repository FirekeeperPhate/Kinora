using Dapper;
using Kinora.Core.MediaServers;

namespace Kinora.Core.Data;

/// <summary>What Kinora and the media server agreed on at the last sync.</summary>
public sealed partial class LibraryDatabase
{
    public Dictionary<(PlaybackKind Kind, long Id), SyncBase> GetSyncStates()
    {
        using var c = Open();
        return c.Query<(string Kind, long Id, bool Watched, long PositionMs)>("SELECT kind, item_id, watched, position_ms FROM server_sync")
                .ToDictionary(r => (r.Kind == "movie" ? PlaybackKind.Movie : PlaybackKind.Episode, r.Id), r => new SyncBase(r.Watched, r.PositionMs));
    }

    public void SaveSyncStates(IEnumerable<(PlaybackKind Kind, long Id, bool Watched, long PositionMs)> states)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var (kind, id, watched, positionMs) in states)
            c.Execute("""
                INSERT INTO server_sync (kind, item_id, watched, position_ms) VALUES (@kind, @id, @watched, @positionMs)
                ON CONFLICT (kind, item_id) DO UPDATE SET watched = excluded.watched, position_ms = excluded.position_ms
                """, new { kind = KindText(kind), id, watched, positionMs }, tx);
        tx.Commit();
    }

    /// <summary>Another server or another user: what was agreed with the old one says nothing about this one.</summary>
    public void ClearSyncStates()
    {
        using var c = Open();
        c.Execute("DELETE FROM server_sync");
    }
}

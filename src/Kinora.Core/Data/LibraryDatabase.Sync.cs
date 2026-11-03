using Dapper;
using Kinora.Core.MediaServers;

namespace Kinora.Core.Data;

/// <summary>What Kinora and the media server agreed on at the last sync.</summary>
public sealed partial class LibraryDatabase
{
    public Dictionary<(PlaybackKind Kind, long Id), SyncBase> GetSyncStates()
    {
        using var c = Open();
        return c.Query<(string Kind, long Id, bool Watched, long PositionMs, string? ServerId)>("SELECT kind, item_id, watched, position_ms, server_id FROM server_sync")
                .ToDictionary(r => (r.Kind == "movie" ? PlaybackKind.Movie : PlaybackKind.Episode, r.Id), r => new SyncBase(r.Watched, r.PositionMs, r.ServerId));
    }

    /// <param name="states">A null server id keeps the one known.</param>
    public void SaveSyncStates(IEnumerable<(PlaybackKind Kind, long Id, bool Watched, long PositionMs, string? ServerId)> states)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var (kind, id, watched, positionMs, serverId) in states)
            c.Execute("""
                INSERT INTO server_sync (kind, item_id, watched, position_ms, server_id) VALUES (@kind, @id, @watched, @positionMs, @serverId)
                ON CONFLICT (kind, item_id) DO UPDATE SET watched = excluded.watched, position_ms = excluded.position_ms,
                                                          server_id = COALESCE(excluded.server_id, server_sync.server_id)
                """, new { kind = KindText(kind), id, watched, positionMs, serverId }, tx);
        tx.Commit();
    }

    /// <summary>The title's id on the server, once a sync has paired it.</summary>
    public string? GetServerId(PlaybackKind kind, long id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<string?>("SELECT server_id FROM server_sync WHERE kind = @kind AND item_id = @id", new { kind = KindText(kind), id });
    }

    /// <summary>Another server or another user: what was agreed with the old one says nothing about this one.</summary>
    public void ClearSyncStates()
    {
        using var c = Open();
        c.Execute("DELETE FROM server_sync");
    }
}

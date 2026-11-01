using Kinora.Core.Playback;

namespace Kinora.Core.MediaServers;

public sealed record LocalState(bool Watched, long PositionMs, long LengthMs, long Updated);

public sealed record RemoteState(bool Played, long PositionMs, long RunTimeMs, long LastPlayed);

/// <summary>What the two sides agreed on at the last sync.</summary>
public sealed record SyncBase(bool Watched, long PositionMs);

public enum PositionMove
{
    None,
    ToKinora,
    ClearKinora,
    ToServer,
}

public sealed record SyncDecision(bool Watched, bool WatchedToKinora, bool WatchedToServer, PositionMove Position, long FinalPositionMs);

/// <summary>
/// One title, both sides. With what they agreed on last time it is known which of the two
/// changed, so a "not watched" set on purpose travels too. The first time nothing is known:
/// watched wins, and nothing is ever unmarked.
/// </summary>
public static class SyncMerge
{
    /// <summary>Two places this close are the same place.</summary>
    private const long Tolerance = 5_000;

    public static SyncDecision Decide(LocalState local, RemoteState remote, SyncBase? last)
    {
        bool watched, toKinora = false, toServer = false;
        if (local.Watched == remote.Played)
        {
            watched = local.Watched;
        }
        else if (last is null)
        {
            watched = true;
            toKinora = !local.Watched;
            toServer = !remote.Played;
        }
        else if (local.Watched != last.Watched)
        {
            watched = local.Watched;
            toServer = true;
        }
        else
        {
            watched = remote.Played;
            toKinora = true;
        }

        if (watched)
            return new SyncDecision(true, toKinora, toServer, local.PositionMs > 0 && toKinora ? PositionMove.ClearKinora : PositionMove.None, 0);

        // A place in the first minute or in the credits isn't a place to resume from
        var length = remote.RunTimeMs > 0 ? remote.RunTimeMs : local.LengthMs;
        var there = PlaybackRules.WorthResuming(remote.PositionMs, length) ? remote.PositionMs : 0;
        var here = local.PositionMs;
        var move = Position(here, there, local.Updated, remote.LastPlayed, last);
        return new SyncDecision(false, toKinora, toServer, move, move switch
        {
            PositionMove.ToKinora => there,
            PositionMove.ClearKinora => 0,
            _ => here,
        });
    }

    private static PositionMove Position(long here, long there, long hereUpdated, long thereUpdated, SyncBase? last)
    {
        if (Math.Abs(here - there) <= Tolerance)
            return PositionMove.None;
        if (last is not null)
        {
            var hereMoved = Math.Abs(here - last.PositionMs) > Tolerance;
            var thereMoved = Math.Abs(there - last.PositionMs) > Tolerance;
            if (hereMoved != thereMoved)
                return thereMoved ? Pull(there) : PositionMove.ToServer;
        }
        else if (there == 0)
        {
            return PositionMove.ToServer;   // the first time an empty side says nothing
        }
        else if (here == 0)
        {
            return PositionMove.ToKinora;
        }
        return thereUpdated > hereUpdated ? Pull(there) : PositionMove.ToServer;
    }

    private static PositionMove Pull(long there) => there > 0 ? PositionMove.ToKinora : PositionMove.ClearKinora;
}

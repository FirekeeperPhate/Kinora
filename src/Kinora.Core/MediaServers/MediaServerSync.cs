using Kinora.Core.Data;
using Kinora.Core.Models;

namespace Kinora.Core.MediaServers;

/// <summary>One title found on both sides, and what happens to it.</summary>
public sealed record SyncPair(PlaybackKind Kind, long LocalId, Movie? Movie, Series? Series, Episode? Episode, ServerItem Server, SyncDecision Decision);

public sealed class SyncPlan
{
    public required IReadOnlyList<SyncPair> Pairs { get; init; }

    /// <summary>Movies and episodes the server has and Kinora hasn't.</summary>
    public int NotFound { get; init; }

    /// <summary>No sync with this server yet: what is shown first, and never done on its own.</summary>
    public bool FirstTime { get; init; }

    public int WatchedHere => Pairs.Count(p => p.Decision is { WatchedToKinora: true, Watched: true });
    public int UnwatchedHere => Pairs.Count(p => p.Decision is { WatchedToKinora: true, Watched: false });
    public int WatchedThere => Pairs.Count(p => p.Decision is { WatchedToServer: true, Watched: true });
    public int UnwatchedThere => Pairs.Count(p => p.Decision is { WatchedToServer: true, Watched: false });
    public int PositionsHere => Pairs.Count(p => p.Decision.Position is PositionMove.ToKinora or PositionMove.ClearKinora);
    public int PositionsThere => Pairs.Count(p => p.Decision.Position == PositionMove.ToServer);
    public bool HasChanges => WatchedHere + UnwatchedHere + WatchedThere + UnwatchedThere + PositionsHere + PositionsThere > 0;
}

/// <param name="FirstError">What went wrong with the first title the server refused.</param>
public sealed record SyncResult(int Failed, string? FirstError);

/// <summary>
/// Watched marks and places to resume, between Kinora and an Emby or Jellyfin server. Titles are
/// found by the end of the path (the server sees the same folders under another name), then by
/// the ids, then by name and year. <see cref="PlanAsync"/> changes nothing.
/// </summary>
public sealed class MediaServerSync(LibraryDatabase db, MediaServerClient client, string user)
{
    /// <summary>A show watched on the server this recently goes in "Continue watching".</summary>
    private static readonly TimeSpan Recent = TimeSpan.FromDays(60);

    private sealed record Local(PlaybackKind Kind, long Id, string Path, Movie? Movie, Series? Series, Episode? Episode);

    public async Task<SyncPlan> PlanAsync(CancellationToken ct)
    {
        var items = await client.GetItemsAsync(user, ct);
        var shows = items.Where(i => i.Type == "Series").ToDictionary(i => i.Id);
        var titles = items.Where(i => i.Type is "Movie" or "Episode" && i.Path is not null).ToList();

        var locals = ReadLocals();
        var byFile = locals.ToLookup(l => FileName(l.Path));
        var byKey = new Dictionary<string, List<Local>>();
        foreach (var local in locals)
            foreach (var key in LocalKeys(local))
            {
                if (!byKey.TryGetValue(key, out var list))
                    byKey[key] = list = [];
                list.Add(local);
            }

        var pairs = new Dictionary<(PlaybackKind, long), (Local Local, ServerItem Item)>();
        var unknown = 0;

        // By path first: it says which copy, where the ids only say which movie
        var rest = new List<ServerItem>();
        foreach (var item in titles)
        {
            var path = Clean(item.Path!);
            var hits = byFile[FileName(path)].Where(l => KindOf(item) == l.Kind && path.EndsWith("/" + l.Path, StringComparison.Ordinal)).ToList();
            if (hits.Count == 0)
                rest.Add(item);
            foreach (var hit in hits)
                pairs.TryAdd((hit.Kind, hit.Id), (hit, item));
        }
        foreach (var item in rest)
        {
            var hits = ServerKeys(item, shows).Select(k => byKey.GetValueOrDefault(k)).FirstOrDefault(h => h is not null);
            if (hits is null)
                unknown++;
            foreach (var hit in hits ?? [])
                pairs.TryAdd((hit.Kind, hit.Id), (hit, item));
        }

        var last = db.GetSyncStates();
        var movies = db.GetPlaybackPositions(PlaybackKind.Movie);
        var episodes = db.GetPlaybackPositions(PlaybackKind.Episode);
        return new SyncPlan
        {
            Pairs = pairs.Values.Select(p =>
            {
                var (local, item) = p;
                var kept = (local.Kind == PlaybackKind.Movie ? movies : episodes).GetValueOrDefault(local.Id);
                var here = new LocalState(local.Movie?.Watched ?? local.Episode!.Watched, kept?.PositionMs ?? 0, kept?.LengthMs ?? 0, kept?.Updated ?? 0);
                var there = new RemoteState(item.Played, item.PositionMs, item.RunTimeMs, item.LastPlayed);
                var decision = SyncMerge.Decide(here, there, last.GetValueOrDefault((local.Kind, local.Id)));
                return new SyncPair(local.Kind, local.Id, local.Movie, local.Series, local.Episode, item, decision);
            }).ToList(),
            NotFound = unknown,
            FirstTime = last.Count == 0,
        };
    }

    private List<Local> ReadLocals()
    {
        var locals = db.GetMovies().Select(m => new Local(PlaybackKind.Movie, m.Id, Clean(m.VideoFile).TrimStart('/'), m, null, null)).ToList();
        foreach (var series in db.GetSeries())
            locals.AddRange(db.GetEpisodes(series.Id).Select(e => new Local(PlaybackKind.Episode, e.Id, Clean(e.VideoFile).TrimStart('/'), null, series, e)));
        return locals;
    }

    private static PlaybackKind KindOf(ServerItem item) => item.Type == "Movie" ? PlaybackKind.Movie : PlaybackKind.Episode;

    private static string Clean(string path) => path.Replace('\\', '/').ToLowerInvariant();

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static IEnumerable<string> LocalKeys(Local local)
    {
        if (local.Movie is { } m)
        {
            foreach (var key in new TitleIds(m.ImdbId, m.TmdbId, null, m.Title, m.Year).Keys)
                yield return key;
            if (m.Year is { } year)
                yield return $"name:{SearchText.Normalize(m.Title)}|{year}";
        }
        else if (local.Series is { } s && local.Episode is { Season: { } season, Number: { } number })
        {
            foreach (var key in new TitleIds(s.ImdbId, s.TmdbId, s.TvdbId, s.Title, s.Year).Keys)
                yield return $"{key}|{season}|{number}";
            yield return $"name:{SearchText.Normalize(s.Title)}|{season}|{number}";
        }
    }

    private static IEnumerable<string> ServerKeys(ServerItem item, Dictionary<string, ServerItem> shows)
    {
        if (item.Type == "Movie")
        {
            foreach (var key in new TitleIds(item.ImdbId, item.TmdbId, null, item.Name, item.Year).Keys)
                yield return key;
            if (item.Year is { } year)
                yield return $"name:{SearchText.Normalize(item.Name)}|{year}";
        }
        else if (item is { Season: { } season, Number: { } number })
        {
            if (item.SeriesId is { } id && shows.TryGetValue(id, out var show))
                foreach (var key in new TitleIds(show.ImdbId, show.TmdbId, show.TvdbId, show.Name, show.Year).Keys)
                    yield return $"{key}|{season}|{number}";
            if (item.SeriesName is { } name)
                yield return $"name:{SearchText.Normalize(name)}|{season}|{number}";
        }
    }

    /// <summary>Kinora first, then the server. A title the server refuses is tried again next time.</summary>
    public async Task<SyncResult> ApplyAsync(SyncPlan plan, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        foreach (var pair in plan.Pairs)
            ApplyHere(pair);

        var pushes = plan.Pairs.Where(p => p.Decision.WatchedToServer || p.Decision.Position == PositionMove.ToServer).ToList();
        var failed = new System.Collections.Concurrent.ConcurrentDictionary<SyncPair, string>();
        var done = 0;
        await Parallel.ForEachAsync(pushes, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (pair, token) =>
        {
            try
            {
                if (pair.Decision.WatchedToServer)
                    await client.SetPlayedAsync(user, pair.Server.Id, pair.Decision.Watched, token);
                if (pair.Decision.Position == PositionMove.ToServer)
                    await client.SetPositionAsync(user, pair.Server.Id, pair.Decision.FinalPositionMs, token);
            }
            catch (Exception e) when (e is MediaServerException or HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                failed[pair] = e.Message;
            }
            progress?.Report((Interlocked.Increment(ref done), pushes.Count));
        });

        db.SaveSyncStates(plan.Pairs.Where(p => !failed.ContainsKey(p))
                              .Select(p => (p.Kind, p.LocalId, p.Decision.Watched, p.Decision.FinalPositionMs)));
        return new SyncResult(failed.Count, failed.Values.FirstOrDefault());
    }

    private void ApplyHere(SyncPair pair)
    {
        var d = pair.Decision;
        if (d.WatchedToKinora)
        {
            if (pair.Movie is { } movie)
                db.SetWatched(movie, d.Watched);
            else
                db.SetEpisodesWatched(pair.Series!, [pair.Episode!], d.Watched);
            if (d.Watched && pair.Series is { } show && pair.Server.LastPlayed > DateTimeOffset.UtcNow.Subtract(Recent).ToUnixTimeSeconds())
                db.SetSeriesPlayed(show.Id, pair.Server.LastPlayed);
        }
        if (d.Position == PositionMove.ToKinora)
            db.SavePlayback(pair.Kind, pair.LocalId, d.FinalPositionMs, pair.Server.RunTimeMs, pair.Server.LastPlayed);
        else if (d.Position == PositionMove.ClearKinora)
            db.ClearPlayback(pair.Kind, pair.LocalId);
    }
}

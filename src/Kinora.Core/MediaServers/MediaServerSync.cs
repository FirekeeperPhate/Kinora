using Kinora.Core.Data;
using Kinora.Core.Models;

namespace Kinora.Core.MediaServers;

/// <summary>One title found on both sides, and what happens to it.</summary>
/// <param name="Agreed">What the two sides agreed on before this sync; null for a title paired the first time.</param>
public sealed record SyncPair(PlaybackKind Kind, long LocalId, Movie? Movie, Series? Series, Episode? Episode, ServerItem Server, SyncDecision Decision,
                              SyncBase? Agreed = null);

/// <summary>A title of Kinora to sync on its own: the one just played, the page just opened.</summary>
public sealed record SyncTitle(PlaybackKind Kind, long LocalId, Movie? Movie, Series? Series, Episode? Episode)
{
    public static SyncTitle Of(Movie movie) => new(PlaybackKind.Movie, movie.Id, movie, null, null);
    public static SyncTitle Of(Series series, Episode episode) => new(PlaybackKind.Episode, episode.Id, null, series, episode);
}

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
        var shows = new Dictionary<string, ServerItem>();
        foreach (var show in items.Where(i => i.Type == "Series"))
            shows.TryAdd(show.Id, show);   // a library changing during the paging can repeat one
        var titles = items.Where(i => i.Type is "Movie" or "Episode" && i.Path is not null).ToList();

        var locals = ReadLocals();
        var byFile = locals.ToLookup(l => FileName(l.Path));
        var byKey = new Dictionary<string, List<Local>>();
        var byName = new Dictionary<string, List<Local>>();
        foreach (var local in locals)
        {
            foreach (var key in LocalKeys(local))
                Add(byKey, key, local);
            if (LocalName(local) is { } name)
                Add(byName, name, local);
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
            var hits = ServerKeys(item, shows).Select(k => byKey.GetValueOrDefault(k)).FirstOrDefault(h => h is not null)
                       ?? Namesakes(item, shows, byName);
            if (hits.Count == 0)
                unknown++;
            foreach (var hit in hits)
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
                var agreed = last.GetValueOrDefault((local.Kind, local.Id));
                var decision = SyncMerge.Decide(here, there, agreed);
                return new SyncPair(local.Kind, local.Id, local.Movie, local.Series, local.Episode, item, decision, agreed);
            }).ToList(),
            NotFound = unknown,
            FirstTime = last.Count == 0,
        };
    }

    /// <summary>
    /// The same, for a few titles only, asked by their id on the server. Only titles a sync has
    /// already paired: the others wait for the next whole sync.
    /// </summary>
    public async Task<SyncPlan> PlanForAsync(IReadOnlyCollection<SyncTitle> titles, CancellationToken ct)
    {
        var last = db.GetSyncStates();
        var paired = titles.Select(t => (Title: t, Base: last.GetValueOrDefault((t.Kind, t.LocalId))))
                           .Where(x => x.Base?.ServerId is not null)
                           .ToList();
        if (paired.Count == 0)
            return new SyncPlan { Pairs = [] };
        var items = (await client.GetItemsByIdsAsync(user, paired.Select(x => x.Base!.ServerId!).ToList(), ct))
                    .GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var movies = db.GetPlaybackPositions(PlaybackKind.Movie);
        var episodes = db.GetPlaybackPositions(PlaybackKind.Episode);
        var pairs = new List<SyncPair>();
        foreach (var (title, sync) in paired)
        {
            if (!items.TryGetValue(sync!.ServerId!, out var item) || KindOf(item) != title.Kind || (title.Movie ?? (object?)title.Episode) is null)
                continue;   // gone from the server, or now something else there
            var kept = (title.Kind == PlaybackKind.Movie ? movies : episodes).GetValueOrDefault(title.LocalId);
            var here = new LocalState(title.Movie?.Watched ?? title.Episode!.Watched, kept?.PositionMs ?? 0, kept?.LengthMs ?? 0, kept?.Updated ?? 0);
            var there = new RemoteState(item.Played, item.PositionMs, item.RunTimeMs, item.LastPlayed);
            pairs.Add(new SyncPair(title.Kind, title.LocalId, title.Movie, title.Series, title.Episode, item, SyncMerge.Decide(here, there, sync), sync));
        }
        return new SyncPlan { Pairs = pairs };
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

    private static void Add(Dictionary<string, List<Local>> index, string key, Local local)
    {
        if (!index.TryGetValue(key, out var list))
            index[key] = list = [];
        list.Add(local);
    }

    /// <summary>The ids a title goes by: for an episode, its show's.</summary>
    private static TitleIds? LocalIds(Local local) =>
        local.Movie is { } m ? new TitleIds(m.ImdbId, m.TmdbId, null, m.Title, m.Year)
        : local.Series is { } s ? new TitleIds(s.ImdbId, s.TmdbId, s.TvdbId, s.Title, s.Year)
        : null;

    private static TitleIds? ServerIds(ServerItem item, Dictionary<string, ServerItem> shows) =>
        item.Type == "Movie" ? new TitleIds(item.ImdbId, item.TmdbId, null, item.Name, item.Year)
        : item.SeriesId is { } id && shows.TryGetValue(id, out var show) ? new TitleIds(show.ImdbId, show.TmdbId, show.TvdbId, show.Name, show.Year)
        : null;

    private static string? NameKey(TitleIds? ids, int? season, int? number) =>
        ids is not { Year: { } year } ? null
        : season is null && number is null ? $"{SearchText.Normalize(ids.Title)}|{year}"
        : $"{SearchText.Normalize(ids.Title)}|{year}|{season}|{number}";

    private static IEnumerable<string> LocalKeys(Local local)
    {
        if (LocalIds(local) is not { } ids)
            yield break;
        if (local.Movie is not null)
            foreach (var key in ids.Keys)
                yield return key;
        else if (local.Episode is { Season: { } season, Number: { } number })
            foreach (var key in ids.Keys)
                yield return $"{key}|{season}|{number}";
    }

    private static string? LocalName(Local local) =>
        local.Movie is not null ? NameKey(LocalIds(local), null, null)
        : local.Episode is { Season: { } season, Number: { } number } ? NameKey(LocalIds(local), season, number)
        : null;

    private static IEnumerable<string> ServerKeys(ServerItem item, Dictionary<string, ServerItem> shows)
    {
        if (ServerIds(item, shows) is not { } ids)
            yield break;
        if (item.Type == "Movie")
            foreach (var key in ids.Keys)
                yield return key;
        else if (item is { Season: { } season, Number: { } number })
            foreach (var key in ids.Keys)
                yield return $"{key}|{season}|{number}";
    }

    /// <summary>
    /// Same name and year, when the ids can't tell. An id of the same kind on both sides that
    /// didn't match says they are two titles: Doctor Who and Doctor Who.
    /// </summary>
    private static List<Local> Namesakes(ServerItem item, Dictionary<string, ServerItem> shows, Dictionary<string, List<Local>> byName)
    {
        var ids = ServerIds(item, shows);
        var name = item.Type == "Movie" ? NameKey(ids, null, null) : NameKey(ids, item.Season, item.Number);
        if (ids is null || name is null || !byName.TryGetValue(name, out var same))
            return [];
        return same.Where(l => LocalIds(l) is { } local && !Differ(local, ids)).ToList();
    }

    private static bool Differ(TitleIds a, TitleIds b) =>
        (a.ImdbId is { Length: > 0 } && b.ImdbId is { Length: > 0 })
        || (a.TmdbId is { Length: > 0 } && b.TmdbId is { Length: > 0 })
        || (a.TvdbId is { Length: > 0 } && b.TvdbId is { Length: > 0 });

    /// <summary>Kinora first, then the server. A title the server refuses is tried again next time.</summary>
    public async Task<SyncResult> ApplyAsync(SyncPlan plan, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var dated = new HashSet<string>();
        foreach (var pair in plan.Pairs)
        {
            ApplyHere(pair);
            // One viewing on the server is one here, also with two copies of the movie
            if (dated.Add(pair.Server.Id))
                KeepDate(pair);
        }

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
            catch (Exception e) when (e is MediaServerException or HttpRequestException or System.Text.Json.JsonException
                                      or TaskCanceledException && !token.IsCancellationRequested)
            {
                failed[pair] = e.Message;
            }
            progress?.Report((Interlocked.Increment(ref done), pushes.Count));
        });

        db.SaveSyncStates(plan.Pairs.Where(p => !failed.ContainsKey(p))
                              .Select(p => (p.Kind, p.LocalId, p.Decision.Watched, p.Decision.FinalPositionMs, (string?)p.Server.Id)));
        return new SyncResult(failed.Count, failed.Values.FirstOrDefault());
    }

    /// <summary>
    /// A title the server has as played, with its date, and none here: its viewing goes into the
    /// history at the server's date. The first sync fills the history with what was watched on the TV.
    /// </summary>
    /// <remarks>
    /// Only what the server says that Kinora didn't tell it: watched there and not here, or a title
    /// paired the first time. A season marked here and pushed gets "played now" on the server,
    /// and that is no viewing.
    /// </remarks>
    private void KeepDate(SyncPair pair)
    {
        if (!pair.Decision.Watched || !pair.Server.Played || pair.Server.LastPlayed <= 0 || db.HasHistory(pair.Kind, pair.LocalId))
            return;
        if (!pair.Decision.WatchedToKinora && pair.Agreed is not null)
            return;
        if (pair.Movie is { } movie)
            db.AddHistory(PlaybackKind.Movie, movie.Id, null, movie.Title, pair.Server.RunTimeMs, pair.Server.LastPlayed);
        else if (pair is { Series: { } show, Episode: { } episode })
            db.AddHistory(PlaybackKind.Episode, episode.Id, show.Id, HistoryTitle(episode), pair.Server.RunTimeMs, pair.Server.LastPlayed);
    }

    /// <summary>"S01E03 · Pilot": how an episode is named in the history.</summary>
    public static string HistoryTitle(Episode episode) =>
        string.Join(" · ", new[] { episode.Code, episode.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));

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

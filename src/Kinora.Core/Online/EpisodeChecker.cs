using System.Collections.Concurrent;
using Kinora.Core.Data;
using Kinora.Core.Models;

namespace Kinora.Core.Online;

/// <param name="Offline">The error is that TMDB couldn't be reached.</param>
public sealed record EpisodeCheckResult(int Shows, int Requests, string? Error, bool Cancelled, bool Offline = false);

/// <summary>
/// Asks TMDB which episodes the shows have, cheaply: a show that has ended is asked once, one
/// still going once a week; the episodes of a season only when the folder lacks some of them.
/// </summary>
public sealed class EpisodeChecker(LibraryDatabase db)
{
    /// <summary>A show still going is asked again after this; the last season too.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromDays(6);

    public int Parallelism { get; set; } = 4;

    /// <summary>The TMDB id of each show of the library that has one (from the folder, or from a match).</summary>
    public List<(Series Show, string TmdbId)> Identified(IEnumerable<Series> shows)
    {
        var result = new List<(Series, string)>();
        foreach (var root in shows.GroupBy(s => s.RootId))
        {
            var online = SeriesOnlineData.Load(db, root.Key);
            foreach (var show in root)
            {
                var id = online.ResolveId(show) ?? (show.NeedsMatch ? online.MatchFor(show.Folder)?.TmdbId : null);
                if (id is not null)
                    result.Add((show, id));
            }
        }
        return result;
    }

    public async Task<EpisodeCheckResult> CheckAsync(IReadOnlyList<(Series Show, string TmdbId)> shows, TmdbClient client,
                                                     IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var fetched = db.GetTmdbSeriesFetched();
        var numbers = Together(shows, db.GetEpisodeNumbers());
        var distinct = shows.DistinctBy(s => s.TmdbId).ToList();
        var offline = false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var today = DateTime.Today;
        var requests = 0;
        var done = 0;
        var errors = new ConcurrentBag<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(distinct, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = stop.Token },
                async (item, token) =>
                {
                    try
                    {
                        var asked = await CheckOneAsync(item.TmdbId, numbers.GetValueOrDefault(item.TmdbId) ?? [], fetched, now, today, client, token);
                        Interlocked.Add(ref requests, asked);
                    }
                    catch (Exception e) when (e is TmdbException or HttpRequestException or System.Text.Json.JsonException
                                              || (e is TaskCanceledException && !token.IsCancellationRequested))
                    {
                        if (e is HttpRequestException or TaskCanceledException)
                            offline = true;
                        errors.Add(e.Message);
                        stop.Cancel();   // a refused key or no connection: the others would fail the same way
                    }
                    progress?.Report((Interlocked.Increment(ref done), distinct.Count));
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            return new EpisodeCheckResult(done, requests, null, Cancelled: true);
        }
        return new EpisodeCheckResult(done, requests, errors.FirstOrDefault(), false, offline && !errors.IsEmpty);
    }

    /// <summary>The episodes of a show by its TMDB id: a show kept in two folders (seasons on two disks) is one show.</summary>
    private static Dictionary<string, List<(int? Season, int? Number, int? NumberEnd)>> Together(
        IReadOnlyList<(Series Show, string TmdbId)> shows, Dictionary<long, List<(int? Season, int? Number, int? NumberEnd)>> numbers) =>
        shows.GroupBy(s => s.TmdbId)
             .ToDictionary(g => g.Key, g => g.SelectMany(s => numbers.GetValueOrDefault(s.Show.Id) ?? []).ToList());

    private async Task<int> CheckOneAsync(string id, List<(int? Season, int? Number, int? NumberEnd)> have, Dictionary<string, long> fetched,
                                          long now, DateTime today, TmdbClient client, CancellationToken ct)
    {
        var asked = 0;
        var show = db.GetTmdbSeries(id);
        var going = show?.Status is not ("Ended" or "Canceled");
        var old = !fetched.TryGetValue(id, out var when) || now - when > Stale.TotalSeconds;
        if (show is null || show.Seasons.Count == 0 || show.Language != client.Language || (going && old))
        {
            asked++;
            show = await client.GetSeriesAsync(id, ct);
            if (show is null)
                return asked;
            db.SaveTmdbSeries(show);
            going = show.Status is not ("Ended" or "Canceled");
        }

        var seasonFetched = db.GetTmdbSeasonsFetched(id);
        var cached = db.GetTmdbSeasons(id).ToDictionary(s => s.Number);
        var latest = EpisodeGaps.LatestAired(show, today);
        foreach (var info in show.Seasons.Where(s => s.Number > 0 && EpisodeGaps.HasAired(s.AirDate, today)))
        {
            var onDisk = have.Where(h => h.Season == info.Number && h.Number is > 0)
                             .SelectMany(h => Enumerable.Range(h.Number!.Value, Math.Max(1, (h.NumberEnd ?? h.Number!.Value) - h.Number!.Value + 1)))
                             .Where(n => n <= info.EpisodeCount)
                             .Distinct().Count();
            var last = going && info.Number == latest;
            if (onDisk >= info.EpisodeCount && !last)
                continue;   // all there
            var stale = !cached.TryGetValue(info.Number, out var season) || season.Language != client.Language
                        || (last && (!seasonFetched.TryGetValue(info.Number, out var at) || now - at > Stale.TotalSeconds));
            if (!stale)
                continue;
            asked++;
            if (await client.GetSeasonAsync(id, info.Number, ct) is { } fresh)
                db.SaveTmdbSeason(id, fresh);
        }
        return asked;
    }

    /// <summary>What each show lacks, from what TMDB said last time: no request.</summary>
    public Dictionary<long, ShowGaps> Gaps(IReadOnlyList<(Series Show, string TmdbId)> shows, DateTime today)
    {
        var raw = db.GetTmdbSeriesRaw().ToDictionary(r => r.TmdbId, r => r.Data);
        var seasons = db.GetAllTmdbSeasons();
        var numbers = Together(shows, db.GetEpisodeNumbers());
        var result = new Dictionary<long, ShowGaps>();
        foreach (var (show, id) in shows)
        {
            if (!raw.TryGetValue(id, out var json) || TmdbSeries.FromJson(json) is not { Seasons.Count: > 0 } tmdb)
                continue;
            if (EpisodeGaps.Find(tmdb, seasons.GetValueOrDefault(id) ?? [], numbers.GetValueOrDefault(id) ?? [], today) is { } gaps)
                result[show.Id] = gaps;
        }
        return result;
    }
}

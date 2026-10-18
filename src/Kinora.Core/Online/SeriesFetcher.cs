using System.Collections.Concurrent;
using System.Text;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Scanning;

namespace Kinora.Core.Online;

/// <summary>
/// TMDB for TV shows, on the same rules as for movies: a show without ids is taken only when the
/// match is sure; then the show once, and only the seasons whose episodes lack something.
/// </summary>
public sealed class SeriesFetcher(LibraryDatabase db, SeriesScanner scanner, ImageCache posters)
{
    public int Parallelism { get; set; } = 4;

    public List<Series> Candidates(LibraryRoot root, IEnumerable<Series> shows, bool retryNotFound)
    {
        var online = SeriesOnlineData.Load(db, root.Id);
        var result = new List<Series>();
        foreach (var s in shows.Where(s => s.RootId == root.Id))
        {
            if (s.NeedsMatch)
            {
                var match = online.MatchFor(s.Folder);
                if (match is null || (match.TmdbId is null ? retryNotFound : online.Find(match.TmdbId) is null))
                    result.Add(s);
                continue;
            }
            var id = online.ResolveId(s);
            if (online.Find(id) is null)
            {
                if (HasGaps(s, db.GetEpisodes(s.Id), db.GetSeasons(s.Id)))
                    result.Add(s);
                continue;
            }
            var cachedSeasons = db.GetTmdbSeasons(id!).Select(x => x.Number).ToHashSet();
            if (SeasonsToFetch(db.GetEpisodes(s.Id)).Any(n => !cachedSeasons.Contains(n)) || (s.ThumbKey is null && s.OnlinePoster is not null))
                result.Add(s);
        }
        return result;
    }

    private static bool HasGaps(Series s, List<Episode> episodes, List<Season> seasons) =>
        s.Plot is null || s.Genres.Length == 0 || s.PosterFile is null || s.FanartFile is null
        || seasons.Any(x => x.PosterFile is null) || SeasonsToFetch(episodes).Any();

    /// <summary>Seasons with an episode that lacks its plot or a picture.</summary>
    private static IEnumerable<int> SeasonsToFetch(List<Episode> episodes) =>
        episodes.Where(e => e.Season is not null && (e.Plot is null || (e.ThumbFile is null && e.OnlineStill is null)))
                .Select(e => e.Season!.Value).Distinct();

    public async Task<FetchResult> FetchAsync(LibraryRoot root, IReadOnlyList<Series> shows, TmdbClient client, bool writeToFolders,
                                              bool refresh, IProgress<FetchProgress>? progress, CancellationToken ct)
    {
        var result = new FetchResult();
        var rebuild = new ConcurrentBag<Series>();
        var writeErrors = new ConcurrentBag<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var matches = db.GetMatches(root.Id);
        int done = 0, identified = 0, completed = 0, notFound = 0, failed = 0, written = 0;

        try
        {
            await Parallel.ForEachAsync(shows, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = stop.Token },
                async (s, token) =>
                {
                    try
                    {
                        var (status, files, error) = await FetchOneAsync(root, s, client, matches.GetValueOrDefault(s.Folder), writeToFolders, refresh, token);
                        if (status == 2) Interlocked.Increment(ref identified);
                        if (status >= 1)
                        {
                            Interlocked.Increment(ref completed);
                            Interlocked.Add(ref written, files);
                            rebuild.Add(s);
                        }
                        else
                            Interlocked.Increment(ref notFound);
                        if (error is not null)
                            writeErrors.Add(error);
                    }
                    catch (TmdbException e) when (e.IsUnauthorized)
                    {
                        result.Error = e.Message;
                        await stop.CancelAsync();
                    }
                    catch (Exception e) when (e is TmdbException or HttpRequestException
                                              or (TaskCanceledException and not OperationCanceledException { CancellationToken.IsCancellationRequested: true }))
                    {
                        Interlocked.Increment(ref failed);
                    }
                    progress?.Report(new FetchProgress(Interlocked.Increment(ref done), shows.Count, s.Title));
                });
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = result.Error is null;
        }

        if (!rebuild.IsEmpty)
            await scanner.RebuildAsync(root, rebuild.ToList(), CancellationToken.None);

        result.Identified = identified;
        result.Completed = completed;
        result.NotFound = notFound;
        result.Failed = failed;
        result.FilesWritten = written;
        result.WriteErrors.AddRange(writeErrors);
        return result;
    }

    public async Task<FetchResult> IdentifyAsync(LibraryRoot root, Series series, string tmdbId, TmdbClient client, bool writeToFolders, CancellationToken ct)
    {
        db.SaveMatch(root.Id, series.Folder, tmdbId, manual: true);
        var probe = new Series { Id = series.Id, RootId = series.RootId, Folder = series.Folder, Title = series.Title, Year = series.Year };
        var (status, files, error) = await FetchOneAsync(root, probe, client, new MatchInfo { VideoFile = series.Folder, TmdbId = tmdbId, Manual = true },
                                                         writeToFolders, refresh: true, ct);
        await scanner.RebuildAsync(root, [series], CancellationToken.None);
        var result = new FetchResult { Identified = 1, Completed = status >= 1 ? 1 : 0, FilesWritten = files };
        if (error is not null)
            result.WriteErrors.Add(error);
        return result;
    }

    /// <returns>Status 0 not found, 1 completed, 2 identified and completed.</returns>
    private async Task<(int Status, int Files, string? Error)> FetchOneAsync(LibraryRoot root, Series s, TmdbClient client, MatchInfo? match,
                                                                            bool writeToFolders, bool refresh, CancellationToken ct)
    {
        var identified = false;
        var id = s.TmdbId;
        if (id is null && s.NeedsMatch && match?.TmdbId is { } matched)
            id = matched;
        if (id is null && s.TvdbId is not null)
            id = await client.FindTvAsync(s.TvdbId, tvdb: true, ct);
        if (id is null && s.ImdbId is not null)
            id = await client.FindTvAsync(s.ImdbId, tvdb: false, ct);
        if (id is null && s.NeedsMatch)
        {
            var results = await client.SearchTvAsync(s.Title, s.Year, ct);
            if (results.Count == 0 && s.Year is not null)
                results = await client.SearchTvAsync(s.Title, null, ct);
            var (best, sure) = TmdbMatcher.Choose(s.Title, s.Year, results);
            if (best is null || !sure)
            {
                db.SaveMatch(root.Id, s.Folder, null, manual: false);
                return (0, 0, null);
            }
            id = best.Id;
            db.SaveMatch(root.Id, s.Folder, id, manual: false);
            identified = true;
        }
        if (id is null)
            return (0, 0, null);

        var tmdb = refresh ? null : db.GetTmdbSeries(id);
        if (tmdb is null || tmdb.Language != client.Language)
        {
            tmdb = await client.GetSeriesAsync(id, ct);
            if (tmdb is null)
                return (0, 0, null);
            db.SaveTmdbSeries(tmdb);
        }

        var episodes = s.Id == 0 ? [] : db.GetEpisodes(s.Id);
        var cached = refresh ? [] : db.GetTmdbSeasons(id).Where(x => x.Language == client.Language).Select(x => x.Number).ToHashSet();
        var wanted = refresh ? episodes.Where(e => e.Season is not null).Select(e => e.Season!.Value).Distinct() : SeasonsToFetch(episodes);
        foreach (var number in wanted.Where(n => !cached.Contains(n)))
            if (await client.GetSeasonAsync(id, number, ct) is { } season)
                db.SaveTmdbSeason(id, season);

        if (s.PosterFile is null && tmdb.PosterPath is { } posterPath)
        {
            var key = ImageCache.KeyForOnline(posterPath);
            if (!posters.Has(key) && await TmdbImages.DownloadAsync(posterPath, "w500", ct) is { } bytes)
                posters.EnsureFromBytes(key, bytes);
        }

        var files = 0;
        string? error = null;
        if (writeToFolders)
        {
            try
            {
                files = await WriteMissingAsync(root, s, tmdb, s.Id == 0 ? [] : db.GetSeasons(s.Id), ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error = $"{s.Title}: {e.Message}";
            }
        }
        return (identified ? 2 : 1, files, error);
    }

    /// <summary>tvshow.nfo, poster, fanart, logo and season posters the show folder lacks. Never replaces.</summary>
    private static async Task<int> WriteMissingAsync(LibraryRoot root, Series s, TmdbSeries t, List<Season> seasons, CancellationToken ct)
    {
        var folder = RelPath.ToIo(root.Path, s.Folder);
        var existing = new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var written = 0;

        async Task Picture(string? tmdbPath, bool haveLocal, string stem)
        {
            if (tmdbPath is null || haveLocal)
                return;
            var name = stem + Path.GetExtension(tmdbPath);
            if (existing.Contains(name) || await TmdbImages.DownloadAsync(tmdbPath, "original", ct) is not { } bytes)
                return;
            if (SaveNew(Path.Combine(folder, name), bytes))
                written++;
        }

        await Picture(t.PosterPath, s.PosterFile is not null, "poster");
        await Picture(t.BackdropPath, s.FanartFile is not null, "fanart");
        await Picture(t.LogoPath, s.LogoFile is not null, "logo");
        foreach (var season in seasons.Where(x => x.PosterFile is null))
            if (t.SeasonPosters.TryGetValue(season.Number, out var path))
                await Picture(path, false, season.Number == 0 ? "season-specials-poster" : $"season{season.Number:00}-poster");

        if (!existing.Contains("tvshow.nfo")
            && SaveNew(Path.Combine(folder, "tvshow.nfo"), new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(NfoWriter.WriteTvShow(t))).ToArray()))
            written++;
        return written;
    }

    private static bool SaveNew(string path, byte[] data)
    {
        if (File.Exists(path))
            return false;
        var temp = path + ".kinora.tmp";
        File.WriteAllBytes(temp, data);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            File.Delete(temp);
            return false;
        }
    }
}

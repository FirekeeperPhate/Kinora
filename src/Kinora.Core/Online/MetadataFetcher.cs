using System.Collections.Concurrent;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Scanning;

namespace Kinora.Core.Online;

public sealed record FetchProgress(int Done, int Total, string? Current);

public sealed class FetchResult
{
    public int Identified { get; set; }
    public int Completed { get; set; }
    public int NotFound { get; set; }
    public int Failed { get; set; }
    public int FilesWritten { get; set; }
    public bool Cancelled { get; set; }

    /// <summary>A problem that stopped everything (a refused key).</summary>
    public string? Error { get; set; }
    public List<string> WriteErrors { get; } = [];
}

/// <summary>
/// Asks TMDB for what the folders don't say:
/// - a movie without ids is searched by title and year, and taken only when the match is sure
///   (otherwise it stays in "Not identified" for the user to choose);
/// - a movie with ids but with gaps gets its TMDB data once (then it is cached);
/// - posters missing from the folder are downloaded into Kinora's cache;
/// - only with the option on, nfo and pictures missing from the folder are written there.
/// Then the movies are rebuilt from their folders, so what the folder says keeps winning.
/// </summary>
public sealed class MetadataFetcher(LibraryDatabase db, MovieScanner scanner, ImageCache posters)
{
    public int Parallelism { get; set; } = 4;

    /// <summary>The movies of a root worth asking TMDB about.</summary>
    public List<Movie> Candidates(LibraryRoot root, IEnumerable<Movie> movies, ISet<long> withCast, bool retryNotFound)
    {
        var online = OnlineData.Load(db, root.Id);
        return movies.Where(m => m.RootId == root.Id && NeedsFetch(m, withCast.Contains(m.Id), online, retryNotFound)).ToList();
    }

    private static bool NeedsFetch(Movie m, bool hasCast, OnlineData online, bool retryNotFound)
    {
        if (m.NeedsMatch)
        {
            var match = online.MatchFor(m.VideoFile);
            if (match is null)
                return true;
            return match.TmdbId is null ? retryNotFound : online.Find(match.TmdbId, null) is null;
        }
        if (online.Find(m.TmdbId, m.ImdbId) is not null)
            return m.ThumbKey is null && m.OnlinePoster is not null;   // known, only the poster to download
        return MetadataMerger.HasGaps(m, hasCast ? 1 : 0);
    }

    public async Task<FetchResult> FetchAsync(LibraryRoot root, IReadOnlyList<Movie> movies, TmdbClient client, bool writeToFolders,
                                              bool refresh, IProgress<FetchProgress>? progress, CancellationToken ct)
    {
        var result = new FetchResult();
        var rebuild = new ConcurrentBag<Movie>();
        var writeErrors = new ConcurrentBag<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var matches = db.GetMatches(root.Id);
        int done = 0, identified = 0, completed = 0, notFound = 0, failed = 0, written = 0;

        try
        {
            await Parallel.ForEachAsync(movies, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = stop.Token },
                async (m, token) =>
                {
                    try
                    {
                        var outcome = await FetchOneAsync(root, m, client, matches.GetValueOrDefault(m.VideoFile), writeToFolders, refresh, token);
                        switch (outcome.Status)
                        {
                            case Status.Identified: Interlocked.Increment(ref identified); goto case Status.Completed;
                            case Status.Completed:
                                Interlocked.Increment(ref completed);
                                Interlocked.Add(ref written, outcome.Written);
                                rebuild.Add(m);
                                break;
                            case Status.NotFound: Interlocked.Increment(ref notFound); break;
                        }
                        if (outcome.WriteError is not null)
                            writeErrors.Add(outcome.WriteError);
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
                    var n = Interlocked.Increment(ref done);
                    progress?.Report(new FetchProgress(n, movies.Count, m.Title));
                });
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = result.Error is null;
        }

        // Even after a cancel: what was fetched is worth showing
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

    /// <summary>The user chose which TMDB movie a file is.</summary>
    public async Task<FetchResult> IdentifyAsync(LibraryRoot root, Movie movie, string tmdbId, TmdbClient client, bool writeToFolders, CancellationToken ct)
    {
        db.SaveMatch(root.Id, movie.VideoFile, tmdbId, manual: true);
        var probe = new Movie { RootId = movie.RootId, Folder = movie.Folder, VideoFile = movie.VideoFile, Title = movie.Title, Year = movie.Year };
        var outcome = await FetchOneAsync(root, probe, client, new MatchInfo { VideoFile = movie.VideoFile, TmdbId = tmdbId, Manual = true },
                                          writeToFolders, refresh: true, ct);
        await scanner.RebuildAsync(root, [movie], CancellationToken.None);
        var result = new FetchResult { Identified = 1, Completed = outcome.Status == Status.NotFound ? 0 : 1, FilesWritten = outcome.Written };
        if (outcome.WriteError is not null)
            result.WriteErrors.Add(outcome.WriteError);
        return result;
    }

    private enum Status { Completed, Identified, NotFound }

    private sealed record Outcome(Status Status, int Written = 0, string? WriteError = null);

    private async Task<Outcome> FetchOneAsync(LibraryRoot root, Movie m, TmdbClient client, MatchInfo? match,
                                              bool writeToFolders, bool refresh, CancellationToken ct)
    {
        var identified = false;
        var id = m.TmdbId;
        if (id is null && m.NeedsMatch && match?.TmdbId is { } matched)
            id = matched;
        if (id is null && m.ImdbId is not null)
            id = await client.FindByImdbAsync(m.ImdbId, ct);
        if (id is null && m.NeedsMatch)
        {
            var results = await client.SearchMovieAsync(m.Title, m.Year, ct);
            if (results.Count == 0 && m.Year is not null)
                results = await client.SearchMovieAsync(m.Title, null, ct);
            var (best, sure) = TmdbMatcher.Choose(m.Title, m.Year, results);
            if (best is null || !sure)
            {
                db.SaveMatch(root.Id, m.VideoFile, null, manual: false);
                return new Outcome(Status.NotFound);
            }
            id = best.Id;
            db.SaveMatch(root.Id, m.VideoFile, id, manual: false);
            identified = true;
        }
        if (id is null)
            return new Outcome(Status.NotFound);

        var tmdb = refresh ? null : db.GetTmdbMovie(id);
        if (tmdb is null || tmdb.Language != client.Language)
        {
            tmdb = await client.GetMovieAsync(id, ct);
            if (tmdb is null)
                return new Outcome(Status.NotFound);
            db.SaveTmdbMovie(tmdb);
        }

        if (m.PosterFile is null && tmdb.PosterPath is { } posterPath)
        {
            var key = ImageCache.KeyForOnline(posterPath);
            if (!posters.Has(key) && await TmdbImages.DownloadAsync(posterPath, "w500", ct) is { } bytes)
                posters.EnsureFromBytes(key, bytes);
        }

        var written = 0;
        string? writeError = null;
        if (writeToFolders)
        {
            try
            {
                written = await MediaFolderWriter.WriteMissingAsync(root, m, tmdb, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                writeError = $"{m.Title}: {e.Message}";
            }
        }
        return new Outcome(identified ? Status.Identified : Status.Completed, written, writeError);
    }
}

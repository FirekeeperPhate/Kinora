using Kinora.Core.Data;

namespace Kinora.Core.Online;

/// <summary>A title TMDB recommends, as its list gives it.</summary>
public sealed record TmdbSimilar(string Id, string Title, int? Year, string? PosterPath, double? Rating);

/// <summary>
/// TMDB's recommendations for a page: asked when the page opens, kept in the database and asked
/// again after a month (new titles come out). Offline, what was kept; never asked, nothing.
/// </summary>
public sealed class SimilarTitles(LibraryDatabase db)
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    public const int Most = 20;

    public async Task<IReadOnlyList<TmdbSimilar>> GetAsync(TmdbClient? tmdb, bool series, string? tmdbId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tmdbId))
            return [];
        var kept = db.GetTmdbSimilar(series, tmdbId);
        if (kept is { } k && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - k.Fetched < KeptFor.TotalSeconds)
            return k.Titles;
        if (tmdb is null)
            return kept?.Titles ?? [];
        try
        {
            var titles = (await tmdb.GetRecommendationsAsync(series, tmdbId, ct)).Take(Most).ToList();
            db.SaveTmdbSimilar(series, tmdbId, titles);
            return titles;
        }
        catch (Exception e) when (e is HttpRequestException or TmdbException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return kept?.Titles ?? [];
        }
    }
}

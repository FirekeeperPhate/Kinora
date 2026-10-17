using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;

namespace Kinora.Core.Online;

/// <summary>
/// The TMDB side of a scan, loaded once: the matches of the root and the cached TMDB movies
/// (decoded only when a movie asks for them).
/// </summary>
public sealed class OnlineData
{
    private readonly Dictionary<string, MatchInfo> _matches;
    private readonly Dictionary<string, string> _jsonById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idByImdb = new(StringComparer.OrdinalIgnoreCase);

    private OnlineData(Dictionary<string, MatchInfo> matches) => _matches = matches;

    public static OnlineData Load(LibraryDatabase db, long rootId)
    {
        var data = new OnlineData(db.GetMatches(rootId));
        foreach (var (id, imdb, json) in db.GetTmdbMoviesRaw())
        {
            data._jsonById[id] = json;
            if (imdb is not null)
                data._idByImdb.TryAdd(imdb, id);
        }
        return data;
    }

    public static OnlineData Empty { get; } = new([]);

    public MatchInfo? MatchFor(string videoFile) => _matches.GetValueOrDefault(videoFile);

    public TmdbMovie? Find(string? tmdbId, string? imdbId)
    {
        if (tmdbId is null && imdbId is not null)
            _idByImdb.TryGetValue(imdbId, out tmdbId);
        return tmdbId is not null && _jsonById.TryGetValue(tmdbId, out var json) ? TmdbMovie.FromJson(json) : null;
    }

    /// <summary>
    /// Applies the match and the cached TMDB data to a movie just built from its folder. A
    /// user's choice beats ids found nowhere; ids in the nfo are never replaced.
    /// </summary>
    public void Apply(Movie movie, List<CastMember> cast, bool titleIsGuess, ImageCache posters)
    {
        if (movie.NeedsMatch && MatchFor(movie.VideoFile) is { TmdbId: { } matched })
            movie.TmdbId = matched;
        if (Find(movie.TmdbId, movie.ImdbId) is not { } tmdb)
            return;
        MetadataMerger.Fill(movie, cast, tmdb, titleIsGuess);
        if (movie.ThumbKey is null && movie.OnlinePoster is { } poster && posters.Has(ImageCache.KeyForOnline(poster)))
            movie.ThumbKey = ImageCache.KeyForOnline(poster);
    }
}

using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;

namespace Kinora.Core.Online;

/// <summary>
/// The TMDB side of a TV scan: matches of the root's show folders and the cached shows and
/// seasons. Fills gaps only; what the folders say wins.
/// </summary>
public sealed class SeriesOnlineData
{
    private readonly LibraryDatabase _db;
    private readonly Dictionary<string, MatchInfo> _matches;
    private readonly Dictionary<string, string> _jsonById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idByImdb = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _idByTvdb = new(StringComparer.Ordinal);

    private SeriesOnlineData(LibraryDatabase db, Dictionary<string, MatchInfo> matches)
    {
        _db = db;
        _matches = matches;
    }

    public static SeriesOnlineData Load(LibraryDatabase db, long rootId)
    {
        var data = new SeriesOnlineData(db, db.GetMatches(rootId));
        foreach (var (id, imdb, tvdb, json) in db.GetTmdbSeriesRaw())
        {
            data._jsonById[id] = json;
            if (imdb is not null) data._idByImdb.TryAdd(imdb, id);
            if (tvdb is not null) data._idByTvdb.TryAdd(tvdb, id);
        }
        return data;
    }

    public MatchInfo? MatchFor(string folder) => _matches.GetValueOrDefault(folder);

    public string? ResolveId(Series s)
    {
        if (s.TmdbId is not null) return s.TmdbId;
        if (s.TvdbId is not null && _idByTvdb.TryGetValue(s.TvdbId, out var byTvdb)) return byTvdb;
        if (s.ImdbId is not null && _idByImdb.TryGetValue(s.ImdbId, out var byImdb)) return byImdb;
        return null;
    }

    public TmdbSeries? Find(string? tmdbId) =>
        tmdbId is not null && _jsonById.TryGetValue(tmdbId, out var json) ? TmdbSeries.FromJson(json) : null;

    /// <param name="guessedTitles">Episodes whose title came from the file name: TMDB's is better.</param>
    public void Apply(Series s, List<CastMember> cast, List<Season> seasons, List<Episode> episodes,
                      bool showTitleIsGuess, ISet<Episode> guessedTitles, ImageCache posters)
    {
        if (s.NeedsMatch && MatchFor(s.Folder) is { TmdbId: { } matched })
            s.TmdbId = matched;
        var id = ResolveId(s);
        if (Find(id) is not { } t)
            return;

        s.TmdbId ??= t.Id;
        s.ImdbId ??= t.ImdbId;
        s.TvdbId ??= t.TvdbId;
        if ((showTitleIsGuess || s.Title.Length == 0) && t.Name.Length > 0)
            s.Title = t.Name;
        s.OriginalTitle ??= t.OriginalName;
        s.Year ??= t.Year;
        s.EndYear ??= t.EndYear;
        s.Plot ??= t.Overview;
        s.Rating ??= t.VoteAverage;
        s.Mpaa ??= t.Certification;
        s.Premiered ??= t.FirstAirDate;
        s.Status ??= t.Status;
        if (s.Genres.Length == 0) s.Genres = Movie.Join(t.Genres);
        if (s.Studios.Length == 0) s.Studios = Movie.Join(t.Networks);
        if (cast.Count == 0)
            cast.AddRange(t.Cast.Select(c => new CastMember { Name = c.Name, Role = c.Character }));
        if (s.PosterFile is null) s.OnlinePoster = t.PosterPath;
        if (s.FanartFile is null) s.OnlineFanart = t.BackdropPath;
        if (s.LogoFile is null) s.OnlineLogo = t.LogoPath;
        if (s.ThumbKey is null && s.OnlinePoster is { } poster && posters.Has(ImageCache.KeyForOnline(poster)))
            s.ThumbKey = ImageCache.KeyForOnline(poster);

        var tmdbSeasons = _db.GetTmdbSeasons(t.Id).ToDictionary(x => x.Number);
        foreach (var season in seasons.Where(x => x.PosterFile is null))
            season.OnlinePoster = tmdbSeasons.GetValueOrDefault(season.Number)?.PosterPath ?? t.SeasonPosters.GetValueOrDefault(season.Number);

        foreach (var e in episodes)
        {
            if (e.Season is not { } sn || e.Number is not { } en || !tmdbSeasons.TryGetValue(sn, out var ts))
                continue;
            var te = ts.Episodes.FirstOrDefault(x => x.Number == en);
            if (te is null)
                continue;
            if ((guessedTitles.Contains(e) || e.Title.Length == 0) && te.Name.Length > 0)
                e.Title = e.NumberEnd is { } end && end != en && ts.Episodes.FirstOrDefault(x => x.Number == end) is { } last
                    ? $"{te.Name} / {last.Name}"
                    : te.Name;
            e.Plot ??= te.Overview;
            e.Aired ??= te.AirDate;
            e.Runtime ??= te.Runtime;
            e.Rating ??= te.VoteAverage;
            if (e.ThumbFile is null) e.OnlineStill = te.StillPath;
        }
    }
}

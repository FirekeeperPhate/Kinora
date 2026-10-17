using Kinora.Core.Models;

namespace Kinora.Core.Online;

/// <summary>
/// Fills the gaps of a movie built from its folder with what TMDB said. Never overwrites: what
/// the folder says (the nfo, the pictures) always wins.
/// </summary>
public static class MetadataMerger
{
    /// <param name="titleIsGuess">
    /// The title came from the file name, not from an nfo: TMDB's is better.
    /// </param>
    public static void Fill(Movie m, List<CastMember> cast, TmdbMovie t, bool titleIsGuess)
    {
        m.TmdbId ??= t.Id;
        m.ImdbId ??= t.ImdbId;
        if ((titleIsGuess || string.IsNullOrWhiteSpace(m.Title)) && t.Title.Length > 0)
            m.Title = t.Title;
        m.OriginalTitle ??= t.OriginalTitle;
        m.Year ??= t.Year;
        m.Plot ??= t.Overview;
        m.Tagline ??= t.Tagline;
        m.Runtime ??= t.Runtime;
        m.Rating ??= t.VoteAverage;
        m.Mpaa ??= t.Certification;
        m.Premiered ??= t.ReleaseDate;
        m.Collection ??= t.Collection;
        if (m.Genres.Length == 0) m.Genres = Movie.Join(t.Genres);
        if (m.Studios.Length == 0) m.Studios = Movie.Join(t.Studios);
        if (m.Countries.Length == 0) m.Countries = Movie.Join(t.Countries);
        if (m.Directors.Length == 0) m.Directors = Movie.Join(t.Directors);
        if (cast.Count == 0)
            cast.AddRange(t.Cast.Select(c => new CastMember { Name = c.Name, Role = c.Character }));
        if (m.PosterFile is null) m.OnlinePoster = t.PosterPath;
        if (m.FanartFile is null) m.OnlineFanart = t.BackdropPath;
        if (m.LogoFile is null) m.OnlineLogo = t.LogoPath;
    }

    /// <summary>What is still missing that TMDB could give; nothing missing, nothing to ask.</summary>
    public static bool HasGaps(Movie m, int castCount) =>
        m.Plot is null || m.Year is null || m.Genres.Length == 0 || castCount == 0
        || (m.PosterFile is null && m.OnlinePoster is null)
        || (m.FanartFile is null && m.OnlineFanart is null);
}

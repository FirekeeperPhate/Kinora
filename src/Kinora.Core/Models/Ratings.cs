namespace Kinora.Core.Models;

/// <summary>Which rating the library sorts and filters by.</summary>
public enum RatingSource
{
    /// <summary>The nfo's default one (or TMDB's when there is no nfo).</summary>
    Default,
    Imdb,
    Tmdb,
}

/// <summary>A movie or a show, with its ratings by source.</summary>
public interface IRated
{
    /// <summary>The nfo's default rating, or TMDB's: the one shown when no source is chosen.</summary>
    double? Rating { get; }

    /// <summary>The ratings by source, as the nfo (TMDB's also from TMDB) says them.</summary>
    double? ImdbRating { get; }
    int? ImdbVotes { get; }
    double? TmdbRating { get; }

    /// <summary>The nfo's userrating: the owner's own mark, 1 to 10.</summary>
    double? UserRating { get; }

    /// <summary>IMDb's own rating, from its daily file. Kept apart from the title: a scan rebuilds the title from the nfo.</summary>
    double? ImdbLatestRating { get; }
    int? ImdbLatestVotes { get; }
}

public static class Ratings
{
    /// <summary>IMDb's file when it has the title, else what the nfo said.</summary>
    public static double? Imdb(this IRated t) => t.ImdbLatestRating ?? t.ImdbRating;

    public static int? ImdbVoteCount(this IRated t) => t.ImdbLatestRating is null ? t.ImdbVotes : t.ImdbLatestVotes;

    /// <summary>The rating the library sorts and filters by: the chosen source, or the default one when that source has none.</summary>
    public static double? By(this IRated t, RatingSource source) => source switch
    {
        RatingSource.Imdb => t.Imdb() ?? t.Rating,
        RatingSource.Tmdb => t.TmdbRating ?? t.Rating,
        _ => t.Rating ?? t.Imdb(),
    };
}

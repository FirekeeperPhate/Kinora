using System.Globalization;
using Kinora.Core.Models;
using Kinora.Core.Online;

namespace Kinora.ViewModels;

/// <summary>One rating on a movie or show page: IMDb, TMDB, the owner's own, or the nfo's unnamed one.</summary>
public sealed record RatingBadge(string Value, string? Source, string? Votes, string? Tip, bool IsMine = false)
{
    public bool HasSource => Source is not null;
    public bool HasVotes => Votes is not null;

    public static IReadOnlyList<RatingBadge> For(IRated t)
    {
        var list = new List<RatingBadge>();
        if (t.Imdb() is { } imdb)
        {
            var votes = t.ImdbVoteCount() is { } n ? Format.Votes(n) : null;
            var tip = (votes is null ? "" : $"{t.ImdbVoteCount():N0} votes on IMDb. ")
                      + (t.ImdbLatestRating is null ? "From the nfo." : ImdbRatings.Attribution);
            list.Add(new RatingBadge(Number(imdb), "IMDb", votes, tip));
        }
        if (t.TmdbRating is { } tmdb)
            list.Add(new RatingBadge(Number(tmdb), "TMDB", null, "The Movie Database's users"));
        if (list.Count == 0 && t.Rating is { } rating)
            list.Add(new RatingBadge(Number(rating), null, null, null));
        if (t.UserRating is { } mine)
            list.Add(new RatingBadge(mine.ToString("0.#", CultureInfo.InvariantCulture), "yours", null, "Your own rating, from the nfo", IsMine: true));
        return list;
    }

    private static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

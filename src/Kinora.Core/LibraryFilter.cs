using Kinora.Core.Models;

namespace Kinora.Core;

/// <summary>The toolbar's filters. Null means any; resolution, HDR and collection are for movies.</summary>
public sealed record LibraryFilter
{
    /// <summary>The name for what is below 720p.</summary>
    public const string LowerResolution = "Lower";

    public string? Resolution { get; init; }
    public bool Hdr { get; init; }

    /// <summary>The first year of the decade: 1990.</summary>
    public int? Decade { get; init; }

    public double? MinRating { get; init; }
    public string? Collection { get; init; }

    public bool Allows(int? year, double? rating) =>
        (Decade is not { } decade || (year is { } y && y / 10 * 10 == decade))
        && (MinRating is not { } min || rating >= min);

    public bool Allows(Movie movie) =>
        Allows(movie.Year, movie.Rating)
        && (!Hdr || movie.IsHdr)
        && (Collection is null || string.Equals(Collection, movie.Collection, StringComparison.CurrentCultureIgnoreCase))
        && Resolution switch
        {
            null => true,
            LowerResolution => movie.Resolution is "576p" or "SD",
            _ => Resolution == movie.Resolution,
        };
}

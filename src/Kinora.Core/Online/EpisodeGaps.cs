using System.Globalization;

namespace Kinora.Core.Online;

/// <summary>An episode TMDB lists, already aired, that the show's folder doesn't have.</summary>
/// <param name="IsNew">After the last episode on disk: out since, rather than a hole.</param>
public sealed record MissingEpisode(int Season, int Number, string? Name, string? AirDate, bool IsNew)
{
    public string Code => $"S{Season:00}E{Number:00}";
}

/// <summary>What a show lacks, by TMDB's list.</summary>
/// <param name="WholeSeasons">Seasons of which there is no episode at all.</param>
public sealed record ShowGaps(IReadOnlyList<MissingEpisode> Episodes, IReadOnlyList<int> WholeSeasons)
{
    public int Count => Episodes.Count;
    public int NewCount => Episodes.Count(e => e.IsNew);

    /// <summary>"S02E04, S02E07, Season 3": a few, then how many more.</summary>
    public string Describe(int most = 3)
    {
        var parts = new List<string>();
        foreach (var season in Episodes.GroupBy(e => e.Season))
        {
            if (WholeSeasons.Contains(season.Key))
                parts.Add($"Season {season.Key}");
            else
                parts.AddRange(season.Select(e => e.Code));
        }
        var shown = string.Join(", ", parts.Take(most));
        return parts.Count > most ? $"{shown} and {parts.Count - most} more" : shown;
    }

    /// <summary>For a poster: "2 new episodes", "5 missing".</summary>
    public string Note => NewCount == Count
        ? (Count == 1 ? $"New: {Episodes[0].Code}" : $"{Count} new episodes")
        : $"{Count} missing: {Describe(2)}";
}

/// <summary>
/// Which aired episodes a show's folder lacks. A season TMDB has described episode by episode
/// says what has aired; one known only by its count is taken whole when it is behind us (the show
/// has ended, or later seasons exist). Specials don't count.
/// </summary>
public static class EpisodeGaps
{
    public static ShowGaps? Find(TmdbSeries show, IReadOnlyDictionary<int, TmdbSeason> seasons,
                                 IEnumerable<(int? Season, int? Number, int? NumberEnd)> have, DateTime today)
    {
        var onDisk = have.Where(h => h.Season is > 0 && h.Number is > 0)
                         .SelectMany(h => Enumerable.Range(h.Number!.Value, Math.Max(1, (h.NumberEnd ?? h.Number!.Value) - h.Number!.Value + 1))
                                                    .Select(n => (Season: h.Season!.Value, Number: n)))
                         .ToHashSet();
        if (onDisk.Count == 0)
            return null;   // nothing understood on disk: nothing to compare with

        var ended = show.Status is "Ended" or "Canceled";
        var latest = LatestAired(show, today);
        var aired = new List<(int Season, int Number, string? Name, string? AirDate)>();
        foreach (var info in show.Seasons.Where(s => s.Number > 0).OrderBy(s => s.Number))
        {
            if (seasons.TryGetValue(info.Number, out var details) && details.Episodes.Count > 0)
            {
                foreach (var e in details.Episodes.Where(e => e.Number > 0 && HasAired(e.AirDate, today)))
                    aired.Add((info.Number, e.Number, e.Name, e.AirDate));
            }
            else if (HasAired(info.AirDate, today) && (ended || info.Number < latest))
            {
                for (var n = 1; n <= info.EpisodeCount; n++)
                    aired.Add((info.Number, n, null, null));
            }
        }
        if (aired.Count == 0)
            return null;

        // Numbered another way (absolute, DVD order): many on disk that TMDB doesn't have
        var listed = aired.Select(a => (a.Season, a.Number)).ToHashSet();
        if (onDisk.Count(k => !listed.Contains(k)) > Math.Max(2, onDisk.Count / 10))
            return null;

        var last = onDisk.Max();
        var missing = aired.Where(a => !onDisk.Contains((a.Season, a.Number)))
                           .Select(a => new MissingEpisode(a.Season, a.Number, a.Name, a.AirDate, (a.Season, a.Number).CompareTo(last) > 0))
                           .ToList();
        if (missing.Count == 0)
            return null;
        var whole = missing.Select(m => m.Season).Distinct().Where(s => !onDisk.Any(k => k.Season == s)).ToList();
        return new ShowGaps(missing, whole);
    }

    /// <summary>The last season that has begun: one only announced (no date, no episodes yet) doesn't count.</summary>
    public static int LatestAired(TmdbSeries show, DateTime today) =>
        show.Seasons.Where(s => s.Number > 0 && HasAired(s.AirDate, today)).Select(s => s.Number).DefaultIfEmpty(0).Max();

    public static bool HasAired(string? date, DateTime today) =>
        DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Date <= today.Date;
}

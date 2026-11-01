using Kinora.Core.Models;

namespace Kinora.Core.Playback;

/// <summary>The order episodes are watched in: seasons and numbers, specials on their own at the end.</summary>
public static class EpisodeOrder
{
    private static bool IsSpecial(Episode e) => e.Season is 0 or null;

    public static List<Episode> Sorted(IEnumerable<Episode> episodes) =>
        episodes.OrderBy(e => IsSpecial(e) ? int.MaxValue : e.Season)
                .ThenBy(e => e.Number ?? int.MaxValue)
                .ThenBy(e => e.VideoFile, StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>The first one not seen yet, specials left out; the first of all if everything has been seen.</summary>
    public static Episode? FirstToWatch(IEnumerable<Episode> episodes)
    {
        var ordered = Sorted(episodes);
        return ordered.FirstOrDefault(e => !e.Watched && !IsSpecial(e)) ?? ordered.FirstOrDefault();
    }

    /// <summary>The episode after this one. The last of a show doesn't run on into the specials.</summary>
    public static Episode? After(IEnumerable<Episode> episodes, Episode current) => Neighbour(episodes, current, 1);

    public static Episode? Before(IEnumerable<Episode> episodes, Episode current) => Neighbour(episodes, current, -1);

    private static Episode? Neighbour(IEnumerable<Episode> episodes, Episode current, int step)
    {
        var ordered = Sorted(episodes);
        var at = ordered.FindIndex(e => e.Id == current.Id);
        if (at < 0)
            return null;
        var other = at + step;
        return other >= 0 && other < ordered.Count && IsSpecial(ordered[other]) == IsSpecial(current) ? ordered[other] : null;
    }
}

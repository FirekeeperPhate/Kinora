using Kinora.Core.Data;

namespace Kinora.Core;

/// <summary>How much was watched: a year's worth, and all of it.</summary>
public sealed record WatchSummary(int ThisYear, double HoursThisYear, int AllTime, double HoursAllTime);

public static class WatchHistory
{
    public static WatchSummary Summarize(IEnumerable<HistoryEntry> entries, PlaybackKind kind, DateTime now)
    {
        var mine = entries.Where(e => e.Kind == kind).ToList();
        var year = mine.Where(e => DateTimeOffset.FromUnixTimeSeconds(e.WatchedAt).ToLocalTime().Year == now.Year).ToList();
        static double Hours(IEnumerable<HistoryEntry> list) => list.Sum(e => e.LengthMs) / 3_600_000.0;
        return new WatchSummary(year.Count, Hours(year), mine.Count, Hours(mine));
    }

    /// <summary>"3 Nov", or "3 Nov 2025" for another year.</summary>
    public static string Date(long unixSeconds, DateTime now)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().DateTime;
        return date.ToString(date.Year == now.Year ? "d MMM" : "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }
}

namespace Kinora.Core.Playback;

/// <summary>When a stop counts as "seen", and when it is worth starting from there next time.</summary>
public static class PlaybackRules
{
    /// <summary>Less than this from the start: nothing to resume.</summary>
    public static readonly TimeSpan MinimumResume = TimeSpan.FromMinutes(1);

    /// <summary>From here on the rest is mostly credits: the title counts as watched.</summary>
    public const double WatchedFraction = 0.9;

    public static bool CountsAsWatched(long positionMs, long lengthMs) =>
        lengthMs > 0 && positionMs >= lengthMs * WatchedFraction;

    public static bool WorthResuming(long positionMs, long lengthMs) =>
        positionMs >= MinimumResume.TotalMilliseconds && !CountsAsWatched(positionMs, lengthMs);

    /// <summary>How much of it has been seen, 0-1, for the "started" mark; null for nothing kept.</summary>
    public static double? SeenFraction(Data.PlaybackPosition? kept) =>
        kept is { LengthMs: > 0 } p ? Math.Clamp((double)p.PositionMs / p.LengthMs, 0, 1) : null;

    /// <summary>VLC's track names, for a menu: "Track 1 - [English]" → "English"; "Commentary - [English]" → "Commentary (English)".</summary>
    public static string TrackName(string? raw, int index)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return $"Track {index}";
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"^(.*?)\s*-\s*\[(.+)\]$");
        if (!m.Success)
            return raw.Trim();
        var title = m.Groups[1].Value.Trim();
        var language = m.Groups[2].Value.Trim();
        if (title.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(title, @"^Track \d+$"))
            return language;
        return title.Contains(language, StringComparison.OrdinalIgnoreCase) ? title : $"{title} ({language})";
    }

    /// <summary>"1:02:13", or "4:05" under an hour.</summary>
    public static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

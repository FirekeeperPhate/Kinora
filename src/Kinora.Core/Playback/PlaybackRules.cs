using System.Text.RegularExpressions;

namespace Kinora.Core.Playback;

/// <summary>A chapter of a video, as the player shows it.</summary>
public sealed record PlayerChapter(string Name, long StartMs, long DurationMs);

/// <summary>When a stop counts as "seen", and when it is worth starting from there next time.</summary>
public static partial class PlaybackRules
{
    /// <summary>Less than this from the start: nothing to resume.</summary>
    public static readonly TimeSpan MinimumResume = TimeSpan.FromMinutes(1);

    /// <summary>From here on the rest is mostly credits: the title counts as watched.</summary>
    public const double WatchedFraction = 0.9;

    public static bool CountsAsWatched(long positionMs, long lengthMs) =>
        lengthMs > 0 && positionMs >= lengthMs * WatchedFraction;

    /// <summary>
    /// The end came before the video got near it: a file that stopped answering (the NAS after
    /// the computer's sleep), not a video seen to the end. Nothing gets marked watched for it.
    /// </summary>
    public static bool EndedEarly(long lastPositionMs, long lengthMs) =>
        lengthMs > 0 && !CountsAsWatched(lastPositionMs, lengthMs);

    /// <summary>
    /// Between two looks at the clock the video moves a fraction of a second (a few at most, at
    /// double speed on a busy computer): more than this, unasked, isn't playing but a jump.
    /// </summary>
    public const long LargestStep = 10_000;

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

    /// <summary>The language in VLC's track name: "Commentary - [English]" → "English"; null without one.</summary>
    public static string? TrackLanguage(string? raw)
    {
        var m = System.Text.RegularExpressions.Regex.Match(raw ?? "", @"\[(.+)\]\s*$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// The track that goes with what was chosen last time: the same name, otherwise the same
    /// language; null when this file has neither, and then VLC's own choice stays.
    /// </summary>
    public static int? PickTrack(IReadOnlyList<(int Id, string Name, string? Language)> tracks, Data.TrackMemory? remembered)
    {
        if (remembered is null)
            return null;
        foreach (var t in tracks)
            if (t.Name.Equals(remembered.Name, StringComparison.OrdinalIgnoreCase))
                return t.Id;
        if (remembered.Language is { Length: > 0 } language)
            foreach (var t in tracks)
                if (language.Equals(t.Language, StringComparison.OrdinalIgnoreCase))
                    return t.Id;
        return null;
    }

    /// <summary>"1:02:13", or "4:05" under an hour.</summary>
    public static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>VLC's chapters, in order, each with a name ("Chapter 3" when the file has none).</summary>
    public static List<PlayerChapter> Chapters(IReadOnlyList<(string? Name, long StartMs, long DurationMs)> found) =>
        found.OrderBy(c => c.StartMs)
             .Select((c, i) => new PlayerChapter(string.IsNullOrWhiteSpace(c.Name) ? $"Chapter {i + 1}" : c.Name.Trim(), Math.Max(0, c.StartMs), Math.Max(0, c.DurationMs)))
             .ToList();

    /// <summary>
    /// A chapter called Intro, Opening, OP or Title sequence, in the first part of the video, is
    /// the intro. More than five minutes is a part of the story with an unlucky name.
    /// </summary>
    public static (long StartMs, long EndMs)? IntroChapter(IReadOnlyList<PlayerChapter> chapters)
    {
        for (var i = 0; i < chapters.Count; i++)
        {
            var c = chapters[i];
            if (!IntroName().IsMatch(c.Name) || c.StartMs > 15 * 60_000)
                continue;
            var end = c.DurationMs > 0 ? c.StartMs + c.DurationMs : i + 1 < chapters.Count ? chapters[i + 1].StartMs : 0;
            if (end > c.StartMs && end - c.StartMs <= 5 * 60_000)
                return (c.StartMs, end);
        }
        return null;
    }

    [GeneratedRegex(@"^\s*(intro|introduction|opening( credits| titles)?|op|title sequence|main titles?|sigla( iniziale)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex IntroName();
}

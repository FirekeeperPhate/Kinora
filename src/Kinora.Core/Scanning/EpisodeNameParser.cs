using System.Text.RegularExpressions;

namespace Kinora.Core.Scanning;

public sealed record ParsedEpisode(int? Season, int? Number, int? NumberEnd, string? Title);

/// <summary>
/// Season and episode from file and folder names: "S01E02", "s1e2", "S04E10-E11", "S01E01E02",
/// "1x02", "Episode 5" or "05 - Title" inside a season folder; "Season 01", "S2", "Specials".
/// </summary>
public static partial class EpisodeNameParser
{
    public static ParsedEpisode Parse(string fileName, int? folderSeason)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);

        if (SxxEyy().Match(stem) is { Success: true } m)
            return new ParsedEpisode(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), End(m.Groups[3]), TitleAfter(stem, m));
        if (NxNN().Match(stem) is { Success: true } x)
            return new ParsedEpisode(int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value), End(x.Groups[3]), TitleAfter(stem, x));
        if (folderSeason is not null)
        {
            if (EpisodeWord().Match(stem) is { Success: true } w)
                return new ParsedEpisode(folderSeason, int.Parse(w.Groups[1].Value), null, TitleAfter(stem, w));
            if (LeadingNumber().Match(stem) is { Success: true } n)
                return new ParsedEpisode(folderSeason, int.Parse(n.Groups[1].Value), null, TitleAfter(stem, n));
        }
        return new ParsedEpisode(folderSeason, null, null, Clean(stem));
    }

    /// <summary>The season a folder holds; 0 for specials; null when the name doesn't say.</summary>
    public static int? SeasonOfFolder(string folderName)
    {
        if (Specials().IsMatch(folderName))
            return 0;
        if (SeasonWord().Match(folderName) is { Success: true } s)
            return int.Parse(s.Groups[1].Value);
        if (ShortSeason().Match(folderName) is { Success: true } sh)
            return int.Parse(sh.Groups[1].Value);
        return null;
    }

    private static int? End(Group g) => g.Success ? int.Parse(g.Value) : null;

    private static string? TitleAfter(string stem, Match m)
    {
        var title = Clean(stem[(m.Index + m.Length)..]);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    /// <summary>"- Pilot Bluray-1080p" → "Pilot"; ".-.Feeding.Frenzy.-.SD.DVD" → "Feeding Frenzy".</summary>
    private static string Clean(string text)
    {
        if (!text.Contains(' ') && text.Count(c => c is '.' or '_') >= 2)
            text = text.Replace('.', ' ').Replace('_', ' ');
        var tag = QualityTag().Match(text);
        if (tag.Success)
            text = text[..tag.Index];
        return MultiSpace().Replace(text, " ").Trim(' ', '-', '.', '_', '[', '(');
    }

    [GeneratedRegex(@"[Ss](\d{1,2})[ ._-]?[Ee](\d{1,3})(?:(?:[-_ ]?[Ee]|-)(\d{1,3}))?(?!\d)")]
    private static partial Regex SxxEyy();

    [GeneratedRegex(@"(?<![\dA-Za-z])(\d{1,2})x(\d{2,3})(?:-(\d{2,3}))?(?!\d)")]
    private static partial Regex NxNN();

    [GeneratedRegex(@"(?:^|[\s._-])(?:episode|ep|e)[\s._-]?(\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWord();

    [GeneratedRegex(@"^(\d{1,3})(?=[\s._-]|$)")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^(?:specials?|season\s*0+|extras?)$|^speciali$", RegexOptions.IgnoreCase)]
    private static partial Regex Specials();

    [GeneratedRegex(@"(?:season|series|stagione|staffel|saison|temporada)[\s._-]*(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonWord();

    [GeneratedRegex(@"(?:^|[\s._-])[Ss](\d{1,2})(?:$|[\s._-])")]
    private static partial Regex ShortSeason();

    [GeneratedRegex(@"(?:^|[\s._\-\[(])(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd|sd|bluray|blu-ray|bdrip|brrip|remux|webrip|web-dl|webdl|hdtv|hdrip|dvdrip|dvd|x264|x265|h\.?264|h\.?265|hevc|xvid|10bit|proper|repack)(?=$|[\s._\-\])])", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTag();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}

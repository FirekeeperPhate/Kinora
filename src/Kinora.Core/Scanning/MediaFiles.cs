using System.Globalization;
using System.Text.RegularExpressions;

namespace Kinora.Core.Scanning;

/// <summary>Which files are videos, subtitles and pictures, and which folders to leave alone.</summary>
public static partial class MediaFiles
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts",
        ".webm", ".flv", ".ogv", ".divx", ".iso", ".vob", ".3gp",
    };

    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".smi",
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".bmp",
    };

    /// <summary>
    /// NAS and OS housekeeping folders (Synology's @eaDir, recycle bins…) and the extras folders
    /// that live inside a movie folder.
    /// </summary>
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "#recycle", "#snapshot", "$RECYCLE.BIN", "System Volume Information", ".deletedByTMM",
        "extrafanart", "extrathumbs", "extras", "featurettes", "behind the scenes", "deleted scenes",
        "interviews", "scenes", "shorts", "trailers", "samples", "sample", "other", "metadata", ".actors",
        "lost+found",
    };

    public static bool IsVideo(string fileName) => VideoExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsIgnoredFolder(string name) =>
        name.StartsWith('.') || IgnoredFolders.Contains(name);

    /// <summary>Trailers and samples that sit next to the movie and must not be taken for it.</summary>
    public static bool IsExtraVideo(string fileName) =>
        ExtraVideo().IsMatch(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>
    /// The language tags of a subtitle next to a video: "Movie.en.srt" -> "en",
    /// "Movie.eng.forced.srt" -> "en forced", "Movie.srt" -> "" (unknown). Null when the file is
    /// not a subtitle of that video.
    /// </summary>
    public static string? SubtitleTag(string fileName, string videoBaseName)
    {
        if (!SubtitleExtensions.Contains(Path.GetExtension(fileName)))
            return null;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(videoBaseName, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = stem[videoBaseName.Length..];
        if (rest.Length > 0 && rest[0] is not ('.' or '_' or '-' or ' '))
            return null;
        var raw = rest.Split(['.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        // "Movie (2025).eng.srt" belongs to "Movie (2025).mp4", not to "Movie.mp4" next to it
        if (raw.Any(p => !p.All(char.IsLetter)))
            return null;
        var parts = raw.Select(p => p.ToLowerInvariant())
                        .Select(p => p is "forced" or "sdh" or "cc" or "hi" or "default" ? p : LanguageCode(p))
                        .ToList();
        return string.Join(' ', parts);
    }

    /// <summary>"eng" -> "en", "ita" -> "it"; anything unknown is kept as written.</summary>
    public static string LanguageCode(string tag)
    {
        if (tag.Length == 3 && ThreeLetter.Value.TryGetValue(tag, out var two))
            return two;
        return tag;
    }

    private static readonly Lazy<Dictionary<string, string>> ThreeLetter = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.TwoLetterISOLanguageName.Length != 2)
                continue;
            map.TryAdd(culture.ThreeLetterISOLanguageName, culture.TwoLetterISOLanguageName);
        }
        // The bibliographic codes some tools use instead of the ISO 639-2/T ones
        foreach (var (b, t) in new[] { ("ger", "de"), ("fre", "fr"), ("dut", "nl"), ("chi", "zh"), ("cze", "cs"), ("gre", "el"), ("per", "fa"), ("rum", "ro"), ("slo", "sk"), ("alb", "sq"), ("arm", "hy"), ("baq", "eu"), ("bur", "my"), ("geo", "ka"), ("ice", "is"), ("mac", "mk"), ("mao", "mi"), ("may", "ms"), ("wel", "cy"), ("tib", "bo") })
            map.TryAdd(b, t);
        return map;
    });

    // Only as a suffix ("Movie-trailer", "Movie.sample2"), the way Kodi and Emby name extras:
    // a movie called "Scene of the Crime" is not an extra.
    [GeneratedRegex(@"(?:^|[\s._\-])(?:sample|trailer|teaser|featurette|behindthescenes|deleted|interview|scene|short|clip)\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraVideo();
}

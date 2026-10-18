namespace Kinora.Core.Subtitles;

/// <summary>Orders search results and saves a subtitle next to its video, with the names players read.</summary>
public static class SubtitleFiles
{
    /// <summary>
    /// Best first: made for this exact file (hash), then the preferred languages in their order,
    /// human translations before machine ones, then the most downloaded.
    /// </summary>
    public static List<SubtitleResult> Rank(IEnumerable<SubtitleResult> results, IReadOnlyList<string> languages)
    {
        int LanguageRank(string lang)
        {
            for (var i = 0; i < languages.Count; i++)
                if (string.Equals(languages[i], lang, StringComparison.OrdinalIgnoreCase)
                    || lang.StartsWith(languages[i] + "-", StringComparison.OrdinalIgnoreCase))
                    return i;
            return languages.Count;
        }

        return results.OrderByDescending(r => r.HashMatch)
                      .ThenBy(r => LanguageRank(r.Language))
                      .ThenBy(r => r.MachineTranslated)
                      .ThenByDescending(r => r.Downloads)
                      .ToList();
    }

    /// <summary>"Movie.mkv" + English → "Movie.en.srt"; hearing impaired "Movie.en.sdh.srt"; forced "Movie.en.forced.srt".</summary>
    public static string TargetPath(string videoPath, SubtitleResult result, string downloadedName)
    {
        var folder = Path.GetDirectoryName(videoPath)!;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        // Names on OpenSubtitles often end in a language ("….eng"): only a real subtitle
        // extension counts, otherwise it is SubRip
        var extension = Path.GetExtension(downloadedName).ToLowerInvariant() is var e
                        && Scanning.MediaFiles.SubtitleExtensions.Contains(e) ? e : ".srt";
        var tag = result.Language.ToLowerInvariant();
        if (result.HearingImpaired) tag += ".sdh";
        if (result.Forced) tag += ".forced";
        return Path.Combine(folder, $"{baseName}.{tag}{extension}");
    }

    /// <summary>
    /// Writes the subtitle. One already there with the same name is kept as ".old" rather than
    /// lost: written aside first, then moved in place.
    /// </summary>
    public static void Save(string target, byte[] content)
    {
        target = ExactPath.ForIo(target);
        var temp = target + ".kinora.tmp";
        File.WriteAllBytes(temp, content);
        if (File.Exists(target))
        {
            var old = Path.Combine(Path.GetDirectoryName(target)!,
                Path.GetFileNameWithoutExtension(target) + ".old" + Path.GetExtension(target));
            File.Move(target, old, overwrite: true);
        }
        File.Move(temp, target, overwrite: false);
    }

    /// <summary>The languages typed in the settings: "en, it" → ["en", "it"].</summary>
    public static List<string> ParseLanguages(string? text) =>
        (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.ToLowerInvariant())
                    .Distinct()
                    .ToList();
}

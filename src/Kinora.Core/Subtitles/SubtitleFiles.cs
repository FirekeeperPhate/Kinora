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
    /// lost: written aside first, then moved in place. A rename over an existing ".old" is refused
    /// now and then by a NAS ("Access to the path is denied": 11 times in 200 on a Synology
    /// share), so the old ".old" is deleted first and the rename goes to a free name. A ".old"
    /// that can't go (open in another program, read-only) leaves its name to ".old2"; a subtitle
    /// that can't be renamed is copied aside and written over where it is.
    /// </summary>
    public static void Save(string target, byte[] content)
    {
        target = ExactPath.ForIo(target);
        var temp = target + ".kinora.tmp";
        var name = Path.GetFileName(target);
        Step($"Couldn't write {name}", () => File.WriteAllBytes(temp, content));
        try
        {
            if (File.Exists(target))
            {
                var old = FreeOldName(target);
                try
                {
                    Patiently(() => File.Move(target, old, overwrite: false));
                }
                catch (IOException) when (File.Exists(target) && !File.Exists(old))
                {
                    // Held open by someone who lets others read and write it, not rename it
                    Step($"Couldn't keep the subtitle already there as {Path.GetFileName(old)}", () => File.Copy(target, old, overwrite: false));
                    Step($"Couldn't write over {name}", () => File.WriteAllBytes(target, content));
                    return;
                }
                catch (UnauthorizedAccessException e)
                {
                    throw Failed($"Couldn't rename the subtitle already there to {Path.GetFileName(old)}", target, e);
                }
            }
            Step($"Couldn't put {name} in place", () => File.Move(temp, target, overwrite: false));
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // Windows names no file when a rename is refused: the message says which step and where
        void Step(string what, Action action)
        {
            try
            {
                Patiently(action);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw Failed(what, target, e);
            }
        }
    }

    private static IOException Failed(string what, string target, Exception e) =>
        new($"{what} in {Path.GetDirectoryName(target)}: {e.Message}", e);

    /// <summary>
    /// A file just written may be held for a moment by whoever looks at new files (an antivirus,
    /// an indexer, a sync on the NAS): a refusal is tried again a few times, 1.5 s in all.
    /// </summary>
    private static void Patiently(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (attempt < 4 && e is UnauthorizedAccessException
                                      or IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
            {
                Thread.Sleep(100 << attempt);
            }
        }
    }

    /// <summary>"Heat.en.srt" → "Heat.en.old.srt", or "Heat.en.old2.srt" if that one is open or read-only, and so on.</summary>
    private static string FreeOldName(string target)
    {
        var folder = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        for (var i = 1; i <= 20; i++)
        {
            var old = Path.Combine(folder, $"{stem}.old{(i == 1 ? "" : i.ToString(System.Globalization.CultureInfo.InvariantCulture))}{extension}");
            if (!File.Exists(old))
                return old;
            try
            {
                File.Delete(old);
                if (!File.Exists(old))
                    return old;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        throw new IOException($"No room to keep the subtitle already there: {Path.GetFileName(target)} and its old copies are all in use.");
    }

    /// <summary>The languages typed in the settings: "en, it" → ["en", "it"].</summary>
    public static List<string> ParseLanguages(string? text) =>
        (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.ToLowerInvariant())
                    .Distinct()
                    .ToList();
}

using System.Xml.Linq;
using Kinora.Core.Scanning;

namespace Kinora.Core.Subtitles;

/// <summary>One episode of the season to give a subtitle to.</summary>
/// <param name="Have">The language tags of the subtitle files next to it, as the scan found them.</param>
public sealed record SubtitleJob(string VideoPath, string Label, SubtitleQuery Query, IReadOnlyList<string> Have);

/// <param name="Stopped">Why it stopped before the end: the daily quota, the key, the network.</param>
/// <param name="Remaining">Downloads left today, as OpenSubtitles said last; -1 when it didn't.</param>
public sealed record SeasonSubtitlesResult(int Saved, int NotFound, int Failed, string? Stopped, int Remaining, IReadOnlyList<string> SavedFiles);

/// <summary>
/// A subtitle for each episode of a season that has none in the chosen languages: the best one
/// by the same order as the search window (made for this file, the first language, by a person,
/// the most downloaded), never forced-only. Each download counts against the daily quota, so it
/// stops when the quota is over.
/// </summary>
public sealed class SeasonSubtitles(OpenSubtitlesClient client)
{
    /// <summary>Between two episodes: the API wants no more than a few requests a second.</summary>
    public TimeSpan Pause { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The episodes that have nothing in the languages: neither a file next to them nor one
    /// inside, as the nfo lists. A forced one (only the foreign lines) doesn't count.
    /// </summary>
    public static bool Lacks(SubtitleJob job, IReadOnlyList<string> languages)
    {
        var full = job.Have.Where(t => !t.Split(' ').Contains("forced")).ToList();
        if (languages.Count == 0)
            return full.Count == 0;
        bool Wanted(string tag) => languages.Any(l => string.Equals(tag.Split(' ')[0], l, StringComparison.OrdinalIgnoreCase));
        return !full.Any(Wanted) && !EmbeddedLanguages(job.VideoPath).Any(Wanted);
    }

    /// <summary>"Episode.mkv" → the subtitle languages Emby or Kodi wrote in "Episode.nfo" (streamdetails).</summary>
    public static List<string> EmbeddedLanguages(string videoPath)
    {
        var nfo = Path.ChangeExtension(ExactPath.ForIo(videoPath), ".nfo");
        try
        {
            if (!File.Exists(nfo))
                return [];
            var text = File.ReadAllText(nfo);
            var start = text.IndexOf('<');
            if (start < 0)
                return [];
            var root = XDocument.Parse("<kinora>" + System.Text.RegularExpressions.Regex.Replace(text[start..], @"<\?xml[^>]*\?>", "") + "</kinora>").Root!;
            return root.Descendants("streamdetails").Descendants("subtitle")
                       .Select(s => (string?)s.Element("language"))
                       .OfType<string>()
                       .Select(l => MediaFiles.LanguageCode(l.Trim().ToLowerInvariant()))
                       .Where(l => l.Length > 0)
                       .Distinct()
                       .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    public async Task<SeasonSubtitlesResult> RunAsync(IReadOnlyList<SubtitleJob> jobs, IReadOnlyList<string> languages, bool skipMachineTranslated,
                                                      Func<string, string, Task>? saved, IProgress<(int Done, int Total, string Label)>? progress,
                                                      CancellationToken ct)
    {
        int found = 0, notFound = 0, failed = 0, remaining = -1, done = 0;
        string? stopped = null;
        var files = new List<string>();
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, jobs.Count, job.Label));
            if (done > 0)
                await Task.Delay(Pause, ct);
            done++;
            try
            {
                string? hash = null;
                try
                {
                    hash = await Task.Run(() => MovieHash.Compute(job.VideoPath), ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
                }
                catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
                {
                }
                var results = await client.SearchAsync(job.Query with { MovieHash = hash }, languages, ct);
                var best = Pick(results, languages, skipMachineTranslated);
                if (best is null)
                {
                    notFound++;
                    continue;
                }
                var file = await client.DownloadAsync(best.FileId, ct);
                var target = SubtitleFiles.TargetPath(job.VideoPath, best, best.FileName);
                var named = SubtitleFiles.TargetPath(job.VideoPath, best, file.FileName);
                if (!string.Equals(Path.GetExtension(named), ".srt", StringComparison.OrdinalIgnoreCase))
                    target = named;
                await Task.Run(() => SubtitleFiles.Save(target, file.Content), ct);
                found++;
                files.Add(target);
                if (saved is not null)
                    await saved(job.VideoPath, target);
                remaining = file.Remaining;
                if (remaining == 0 && done < jobs.Count)
                {
                    stopped = "No downloads left today on OpenSubtitles.";
                    break;
                }
            }
            catch (OpenSubtitlesException e) when (e.Status is System.Net.HttpStatusCode.NotAcceptable or System.Net.HttpStatusCode.Unauthorized
                                                   or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            {
                // The quota (406), the key or the login: the next episodes would fail the same way
                failed++;
                stopped = e.Message;
                break;
            }
            catch (Exception e) when (e is OpenSubtitlesException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                failed++;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failed++;
                stopped = e.Message;
                break;
            }
        }
        progress?.Report((done, jobs.Count, ""));
        return new SeasonSubtitlesResult(found, notFound, failed, stopped, remaining, files);
    }

    /// <summary>The best of the results in the wanted languages; forced-only subtitles are only the foreign lines.</summary>
    public static SubtitleResult? Pick(IEnumerable<SubtitleResult> results, IReadOnlyList<string> languages, bool skipMachineTranslated)
    {
        var usable = results.Where(r => !r.Forced && !(skipMachineTranslated && r.MachineTranslated));
        if (languages.Count > 0)
            usable = usable.Where(r => languages.Any(l => string.Equals(r.Language, l, StringComparison.OrdinalIgnoreCase)
                                                         || r.Language.StartsWith(l + "-", StringComparison.OrdinalIgnoreCase)));
        return SubtitleFiles.Rank(usable, languages).FirstOrDefault();
    }
}

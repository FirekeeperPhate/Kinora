using System.Collections.Concurrent;
using System.Diagnostics;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;

namespace Kinora.Core.Scanning;

/// <summary>
/// Builds the library of a TV root: one folder per show, episodes in season folders or loose in
/// the show folder. A show is entered again only when its folder or one of its season folders
/// changed: one listing of the show folder and one small request per season folder tell.
/// Season posters and episode thumbnails are not copied here (thousands of them): the show page
/// makes them on demand.
/// </summary>
public sealed class SeriesScanner(LibraryDatabase db, ImageCache posters)
{
    public TimeSpan RootTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public int Parallelism { get; set; } = 8;

    private sealed record Built(Series Series, List<CastMember> Cast, List<Season> Seasons, List<Episode> Episodes);

    public async Task<ScanResult> ScanAsync(LibraryRoot root, bool full, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var result = new ScanResult();
        try
        {
            await ScanCoreAsync(root, full, progress, result, ct);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        result.Elapsed = watch.Elapsed;
        return result;
    }

    private async Task ScanCoreAsync(LibraryRoot root, bool full, IProgress<ScanProgress>? progress, ScanResult result, CancellationToken ct)
    {
        progress?.Report(new ScanProgress($"Connecting to {root.Name}", 0, 0, root.Path));
        result.RootAvailable = await IsReachableAsync(root.Path, ct);
        if (!result.RootAvailable)
            return;

        var existing = db.GetSeries(root.Id).ToDictionary(s => s.Folder, StringComparer.OrdinalIgnoreCase);
        List<DirectoryInfo> shows;
        try
        {
            shows = await Task.Run(() => new DirectoryInfo(ExactPath.ForIo(root.Path)).EnumerateDirectories()
                                                                      .Where(d => !MediaFiles.IsHousekeepingFolder(d.Name)).ToList(), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result.RootAvailable = false;
            result.Errors.Add($"{root.Path}: {e.Message}");
            return;
        }

        var online = SeriesOnlineData.Load(db, root.Id);
        var keep = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var built = new ConcurrentBag<Built>();
        var errors = new ConcurrentBag<string>();
        int done = 0, unchanged = 0;

        await Parallel.ForEachAsync(shows, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            (dir, token) =>
            {
                keep[dir.Name] = 0;
                try
                {
                    var b = Visit(root, dir, full, existing.GetValueOrDefault(dir.Name), online);
                    if (b is null)
                        Interlocked.Increment(ref unchanged);
                    else
                        built.Add(b);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{dir.Name}: {e.Message}");
                }
                var n = Interlocked.Increment(ref done);
                progress?.Report(new ScanProgress("Reading shows", n, shows.Count, dir.Name));
                return ValueTask.CompletedTask;
            });

        ct.ThrowIfCancellationRequested();
        var list = built.ToList();
        var saved = 0;
        foreach (var b in list)
        {
            db.SaveSeries(b.Series, b.Cast, b.Seasons, b.Episodes);
            progress?.Report(new ScanProgress("Saving", ++saved, list.Count, b.Series.Title));
        }

        var gone = existing.Values.Where(s => !keep.ContainsKey(s.Folder)).Select(s => s.Id).ToList();
        if (gone.Count > 0)
            db.DeleteSeries(gone);

        result.Added = list.Count(b => !existing.ContainsKey(b.Series.Folder));
        result.Updated = list.Count - result.Added;
        result.Removed = gone.Count;
        result.Unchanged = unchanged;
        result.Errors.AddRange(errors);
    }

    /// <summary>Builds a few shows again (after a TMDB download or a choice by hand).</summary>
    public async Task RebuildAsync(LibraryRoot root, IReadOnlyCollection<Series> shows, CancellationToken ct)
    {
        var online = SeriesOnlineData.Load(db, root.Id);
        var built = new ConcurrentBag<Built>();
        await Parallel.ForEachAsync(shows, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            (s, _) =>
            {
                try
                {
                    var dir = new DirectoryInfo(RelPath.ToIo(root.Path, s.Folder));
                    if (dir.Exists && Visit(root, dir, full: true, db.GetSeriesById(s.Id), online) is { } b)
                        built.Add(b);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
                return ValueTask.CompletedTask;
            });
        foreach (var b in built)
            db.SaveSeries(b.Series, b.Cast, b.Seasons, b.Episodes);
    }

    private Built? Visit(LibraryRoot root, DirectoryInfo dir, bool full, Series? known, SeriesOnlineData online)
    {
        dir.Refresh();
        var entries = dir.EnumerateFileSystemInfos().ToList();
        var files = entries.OfType<FileInfo>().ToList();
        var subdirs = entries.OfType<DirectoryInfo>().Where(d => !MediaFiles.IsIgnoredFolder(d.Name)).ToList();
        foreach (var d in subdirs)
            d.Refresh();   // the parent's copy of a folder's time is lazy on NTFS

        long stamp = dir.LastWriteTimeUtc.Ticks;
        foreach (var d in subdirs)
            stamp = unchecked(stamp * 31 + d.LastWriteTimeUtc.Ticks);
        if (!full && known is not null && known.Stamp == stamp)
            return null;

        var rel = dir.Name;
        var nfo = files.FirstOrDefault(f => f.Name.Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase)) is { } nfoFile
            ? NfoReader.Read(nfoFile.FullName)
            : null;
        var parsed = FileNameParser.Parse(dir.Name);
        var names = files.Select(f => f.Name).ToList();
        var art = LocalArtwork.Find(names, "", ownFolder: true);
        var logo = art.Logo;

        var series = new Series
        {
            Id = known?.Id ?? 0,
            RootId = root.Id,
            Folder = rel,
            Stamp = stamp,
            Title = nfo?.Title ?? parsed.Title,
            OriginalTitle = nfo?.OriginalTitle,
            SortTitle = nfo?.SortTitle,
            Year = nfo?.Year ?? parsed.Year,
            Plot = nfo?.Plot,
            Rating = nfo?.Rating is > 0 ? nfo.Rating : null,
            Mpaa = nfo?.Mpaa,
            Premiered = nfo?.Premiered,
            Status = nfo?.Status,
            ImdbId = nfo?.ImdbId ?? parsed.ImdbId,
            TmdbId = nfo?.TmdbId ?? parsed.TmdbId,
            TvdbId = nfo?.TvdbId,
            Genres = Movie.Join(nfo?.Genres ?? []),
            Studios = Movie.Join(nfo?.Studios ?? []),
            PosterFile = art.Poster is null ? null : RelPath.Combine(rel, art.Poster),
            FanartFile = art.Fanart is null ? null : RelPath.Combine(rel, art.Fanart),
            LogoFile = logo is null ? null : RelPath.Combine(rel, logo),
        };
        if (art.Poster is not null)
        {
            var info = files.First(f => f.Name == art.Poster);
            var key = ImageCache.KeyFor(info.FullName, info.Length, info.LastWriteTimeUtc);
            series.ThumbKey = posters.Ensure(key, info.FullName) ? key : null;
        }

        var knownEpisodes = known is null ? [] : db.GetEpisodes(known.Id).ToDictionary(e => e.VideoFile, StringComparer.OrdinalIgnoreCase);
        var episodes = new List<Episode>();
        var guessed = new HashSet<Episode>(ReferenceEqualityComparer.Instance);

        // Episodes loose in the show folder, then in each season folder
        AddEpisodes(rel, files, null, knownEpisodes, episodes, guessed);
        var subFiles = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in subdirs)
        {
            var season = EpisodeNameParser.SeasonOfFolder(sub.Name);
            subFiles[sub.Name] = sub.EnumerateFiles().ToList();
            AddEpisodes(RelPath.Combine(rel, sub.Name), subFiles[sub.Name], season, knownEpisodes, episodes, guessed);
        }

        var seasons = episodes.Where(e => e.Season is not null).Select(e => e.Season!.Value).Distinct().OrderBy(n => n)
                              .Select(n => new Season { Number = n, PosterFile = SeasonPoster(rel, names, subFiles, n) })
                              .ToList();

        var cast = (nfo?.Actors ?? []).Take(40).Select(a => new CastMember { Name = a.Name, Role = a.Role, TmdbId = a.TmdbId }).ToList();
        online.Apply(series, cast, seasons, episodes, showTitleIsGuess: nfo?.Title is null, guessed, posters);

        var dates = episodes.Select(e => e.DateAdded).DefaultIfEmpty(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToList();
        series.DateAdded = known?.DateAdded ?? (nfo?.DateAdded is { } added ? new DateTimeOffset(added.ToUniversalTime()).ToUnixTimeSeconds() : dates.Min());
        series.LastEpisodeAdded = dates.Max();
        series.EpisodeCount = episodes.Count;
        series.WatchedCount = episodes.Count(e => e.Watched);
        return new Built(series, cast, seasons, episodes);
    }

    private static void AddEpisodes(string rel, List<FileInfo> files, int? folderSeason, Dictionary<string, Episode> known,
                                    List<Episode> episodes, HashSet<Episode> guessed)
    {
        foreach (var video in files.Where(f => MediaFiles.IsVideo(f.Name) && !MediaFiles.IsExtraVideo(f.Name)))
        {
            var baseName = Path.GetFileNameWithoutExtension(video.Name);
            var parsed = EpisodeNameParser.Parse(video.Name, folderSeason);
            var nfoFile = files.FirstOrDefault(f => f.Name.Equals(baseName + ".nfo", StringComparison.OrdinalIgnoreCase));
            var nfo = nfoFile is null ? null : NfoReader.Read(nfoFile.FullName);
            var thumb = files.FirstOrDefault(f => f.Name.Equals(baseName + "-thumb.jpg", StringComparison.OrdinalIgnoreCase)
                                               || f.Name.Equals(baseName + "-thumb.png", StringComparison.OrdinalIgnoreCase)
                                               || f.Name.Equals(baseName + ".tbn", StringComparison.OrdinalIgnoreCase));
            var videoRel = RelPath.Combine(rel, video.Name);

            var e = new Episode
            {
                Season = nfo?.Season ?? parsed.Season,
                Number = nfo?.Episode ?? parsed.Number,
                NumberEnd = parsed.NumberEnd,
                Title = nfo?.Title ?? parsed.Title ?? baseName,
                Plot = nfo?.Plot,
                Aired = nfo?.Premiered,
                Runtime = nfo?.Runtime is > 0 ? nfo.Runtime : null,
                Rating = nfo?.Rating is > 0 ? nfo.Rating : null,
                Directors = Movie.Join(nfo?.Directors ?? []),
                ImdbId = nfo?.ImdbId,
                VideoFile = videoRel,
                VideoSize = video.Length,
                ThumbFile = thumb is null ? null : RelPath.Combine(rel, thumb.Name),
                Resolution = MovieScanner.ResolutionLabel(nfo?.VideoWidth, nfo?.VideoHeight),
                VideoCodec = nfo?.VideoCodec,
                AudioCodec = nfo?.AudioCodec,
                AudioChannels = nfo?.AudioChannels,
                Subtitles = Movie.Join(files.Select(f => MediaFiles.SubtitleTag(f.Name, baseName))
                                            .Where(t => t is not null).Select(t => t!.Length == 0 ? "und" : t)),
                Watched = nfo?.Watched ?? false,
                DateAdded = new DateTimeOffset(nfo?.DateAdded?.ToUniversalTime() is { } d && d < video.LastWriteTimeUtc && d.Year > 1990
                                                   ? d : video.LastWriteTimeUtc).ToUnixTimeSeconds(),
            };
            if (known.TryGetValue(videoRel, out var k))
            {
                e.Id = k.Id;
                e.Watched = k.Watched;
                e.DateAdded = k.DateAdded;
            }
            if (nfo?.Title is null)
                guessed.Add(e);
            episodes.Add(e);
        }
    }

    /// <summary>seasonNN-poster.jpg / seasonNN.tbn in the show folder, or a poster inside the season folder.</summary>
    private static string? SeasonPoster(string rel, List<string> showFiles, Dictionary<string, List<FileInfo>> seasonFolders, int number)
    {
        var stems = number == 0
            ? new[] { "season-specials-poster", "season00-poster", "season-specials", "season00" }
            : new[] { $"season{number:00}-poster", $"season{number}-poster", $"season{number:00}", $"season{number}" };
        foreach (var stem in stems)
            foreach (var name in showFiles)
                if (Path.GetFileNameWithoutExtension(name).Equals(stem, StringComparison.OrdinalIgnoreCase)
                    && MediaFiles.ImageExtensions.Contains(Path.GetExtension(name)))
                    return RelPath.Combine(rel, name);

        foreach (var (folder, folderFiles) in seasonFolders)
        {
            if (EpisodeNameParser.SeasonOfFolder(folder) != number)
                continue;
            foreach (var candidate in new[] { "poster.jpg", "folder.jpg", "poster.png", "folder.png" })
                if (folderFiles.FirstOrDefault(f => f.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) is { } hit)
                    return RelPath.Combine(RelPath.Combine(rel, folder), hit.Name);
        }
        return null;
    }

    public async Task<bool> IsReachableAsync(string path, CancellationToken ct)
    {
        var probe = Task.Run(() => Directory.Exists(path), CancellationToken.None);
        try
        {
            return await probe.WaitAsync(RootTimeout, ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

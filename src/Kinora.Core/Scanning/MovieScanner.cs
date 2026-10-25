using System.Collections.Concurrent;
using System.Diagnostics;
using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;
using Kinora.Core.Online;

namespace Kinora.Core.Scanning;

public sealed record ScanProgress(string Stage, int Done, int Total, string? Current);

public sealed class ScanResult
{
    public bool RootAvailable { get; set; }
    public bool Cancelled { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Unchanged { get; set; }
    public List<string> Errors { get; } = [];
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Builds the library of a movie root. Over a network share the cost is the number of requests,
/// so:
/// - one listing of the root gives every movie folder with its modification time;
/// - a folder whose time hasn't changed since the last scan is not entered at all;
/// - the folders that must be entered are listed in parallel, and one listing already carries
///   names, sizes and times of every file (nfo, pictures and subtitles are picked from it).
/// A full scan enters every folder anyway, for changes that don't touch the folder's time
/// (a file rewritten in place).
/// </summary>
public sealed class MovieScanner(LibraryDatabase db, ImageCache posters)
{
    public TimeSpan RootTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public int Parallelism { get; set; } = 8;
    private const int MaxDepth = 3;

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

        var existing = db.GetMovies(root.Id);
        var byVideo = existing.ToDictionary(m => m.VideoFile, StringComparer.OrdinalIgnoreCase);
        var byFolder = existing.Where(m => m.Folder.Length > 0)
                               .GroupBy(m => m.Folder, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Videos still there (or that we could not check): everything else gets removed at the end
        var keep = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var found = new ConcurrentBag<(Movie Movie, IReadOnlyList<CastMember> Cast)>();
        var errors = new ConcurrentBag<string>();
        var online = OnlineData.Load(db, root.Id);
        var unchanged = 0;

        progress?.Report(new ScanProgress("Listing folders", 0, 0, root.Path));
        List<FileSystemInfo> top;
        try
        {
            top = await Task.Run(() => new DirectoryInfo(ExactPath.ForIo(root.Path)).EnumerateFileSystemInfos().ToList(), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result.RootAvailable = false;
            result.Errors.Add($"{root.Path}: {e.Message}");
            return;
        }

        var dirs = top.OfType<DirectoryInfo>().Where(d => !MediaFiles.IsHousekeepingFolder(d.Name)).ToList();

        // The times that come with a listing are the parent's copy, and NTFS updates that copy
        // lazily: a folder that just changed can still show its old time. Asking each folder is
        // one small request per folder, done in parallel.
        progress?.Report(new ScanProgress("Checking folders", 0, dirs.Count, null));
        await Parallel.ForEachAsync(dirs, new ParallelOptions { MaxDegreeOfParallelism = Parallelism * 2, CancellationToken = ct },
            (d, _) => { d.Refresh(); return ValueTask.CompletedTask; });

        var toVisit = new List<(DirectoryInfo Dir, string Rel)>();
        foreach (var dir in dirs)
        {
            if (!full && IsUnchanged(dir.Name, dir, byFolder, keep))
                unchanged++;
            else
                toVisit.Add((dir, dir.Name));
        }

        // Videos loose in the root: each one is a movie, its stamp follows its own files
        var rootFiles = top.OfType<FileInfo>().ToList();
        foreach (var video in rootFiles.Where(f => MediaFiles.IsVideo(f.Name) && !MediaFiles.IsExtraVideo(f.Name)))
        {
            var baseName = Path.GetFileNameWithoutExtension(video.Name);
            var siblings = rootFiles.Where(f => f.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)).ToList();
            var stamp = siblings.Max(f => f.LastWriteTimeUtc.Ticks);
            keep[video.Name] = 0;
            if (!full && byVideo.TryGetValue(video.Name, out var known) && known.Stamp == stamp)
            {
                unchanged++;
                continue;
            }
            found.Add(Build(root, "", siblings, video, [], ownFolder: false, stamp, byVideo, online));
        }

        var done = 0;
        var total = toVisit.Count;
        progress?.Report(new ScanProgress("Reading folders", 0, total, null));
        await Parallel.ForEachAsync(toVisit, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            (item, token) =>
            {
                Visit(root, item.Dir, item.Rel, 1, full, byFolder, byVideo, keep, found, errors, online, ref unchanged, token);
                var n = Interlocked.Increment(ref done);
                progress?.Report(new ScanProgress("Reading folders", n, total, item.Rel));
                return ValueTask.CompletedTask;
            });

        ct.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress("Saving", total, total, null));

        var items = found.ToList();
        foreach (var chunk in items.Chunk(200))
            db.SaveMovies(chunk);

        var gone = existing.Where(m => !keep.ContainsKey(m.VideoFile)).Select(m => m.Id).ToList();
        if (gone.Count > 0)
            db.DeleteMovies(gone);

        root.LastScan = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.UpdateRoot(root);

        result.Added = items.Count(i => !byVideo.ContainsKey(i.Movie.VideoFile));
        result.Updated = items.Count - result.Added;
        result.Removed = gone.Count;
        result.Unchanged = unchanged;
        result.Errors.AddRange(errors);
    }

    private static bool IsUnchanged(string rel, DirectoryInfo dir, Dictionary<string, List<Movie>> byFolder, ConcurrentDictionary<string, byte> keep)
    {
        if (!byFolder.TryGetValue(rel, out var known))
            return false;
        var stamp = dir.LastWriteTimeUtc.Ticks;
        if (known.Any(m => m.Stamp != stamp))
            return false;
        foreach (var m in known)
            keep[m.VideoFile] = 0;
        return true;
    }

    private void Visit(LibraryRoot root, DirectoryInfo dir, string rel, int depth, bool full,
                       Dictionary<string, List<Movie>> byFolder, Dictionary<string, Movie> byVideo,
                       ConcurrentDictionary<string, byte> keep, ConcurrentBag<(Movie, IReadOnlyList<CastMember>)> found,
                       ConcurrentBag<string> errors, OnlineData online, ref int unchanged, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{rel}: {e.Message}");
            // Can't tell what's in there: keep what we knew rather than deleting it
            foreach (var m in byVideo.Values.Where(m => IsUnder(m.Folder, rel)))
                keep[m.VideoFile] = 0;
            return;
        }

        var files = entries.OfType<FileInfo>().ToList();
        var videos = files.Where(f => MediaFiles.IsVideo(f.Name)).ToList();
        if (videos.Count > 0)
        {
            var features = videos.Where(f => !MediaFiles.IsExtraVideo(f.Name)).ToList();
            if (features.Count == 0)
                features = videos;
            var main = PickMain(features, files);
            var others = features.Where(f => f != main).Select(f => RelPath.Combine(rel, f.Name)).ToList();
            try
            {
                found.Add(Build(root, rel, files, main, others, ownFolder: true, dir.LastWriteTimeUtc.Ticks, byVideo, online));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{rel}: {e.Message}");
            }
            keep[RelPath.Combine(rel, main.Name)] = 0;
            return;
        }

        // No video here: a collection folder ("Alien Collection/Alien (1979)/…")
        if (depth >= MaxDepth)
            return;
        foreach (var sub in entries.OfType<DirectoryInfo>())
        {
            if (MediaFiles.IsIgnoredFolder(sub.Name))
                continue;
            var subRel = RelPath.Combine(rel, sub.Name);
            sub.Refresh();
            if (!full && IsUnchanged(subRel, sub, byFolder, keep))
            {
                Interlocked.Increment(ref unchanged);
                continue;
            }
            Visit(root, sub, subRel, depth + 1, full, byFolder, byVideo, keep, found, errors, online, ref unchanged, ct);
        }
    }

    /// <summary>
    /// With more than one video in a folder: the one the folder's nfo is named after, otherwise
    /// the largest.
    /// </summary>
    private static FileInfo PickMain(List<FileInfo> videos, List<FileInfo> files)
    {
        if (videos.Count == 1)
            return videos[0];
        var withNfo = videos.Where(v => files.Any(f => f.Name.Equals(Path.GetFileNameWithoutExtension(v.Name) + ".nfo", StringComparison.OrdinalIgnoreCase)))
                            .ToList();
        var pool = withNfo.Count > 0 ? withNfo : videos;
        return pool.MaxBy(v => v.Length)!;
    }

    private (Movie, IReadOnlyList<CastMember>) Build(LibraryRoot root, string rel, List<FileInfo> files, FileInfo video,
                                                     List<string> otherVideos, bool ownFolder, long stamp,
                                                     Dictionary<string, Movie> byVideo, OnlineData online)
    {
        var videoBase = Path.GetFileNameWithoutExtension(video.Name);
        var folderName = rel.Length > 0 ? RelPath.LastSegment(rel) : "";

        // movie.nfo and <video>.nfo often both exist: the newer one wins, the older fills gaps
        var nfoFiles = files.Where(f => f.Extension.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                            .Where(f =>
                            {
                                var stem = Path.GetFileNameWithoutExtension(f.Name);
                                return stem.Equals(videoBase, StringComparison.OrdinalIgnoreCase)
                                    || (ownFolder && (stem.Equals("movie", StringComparison.OrdinalIgnoreCase)
                                                      || stem.Equals(folderName, StringComparison.OrdinalIgnoreCase)));
                            })
                            .OrderByDescending(f => f.LastWriteTimeUtc);
        NfoData? nfo = null;
        foreach (var file in nfoFiles)
        {
            if (NfoReader.Read(file.FullName) is not { } data)
                continue;
            if (nfo is null)
                nfo = data;
            else
                nfo.FillFrom(data);
        }

        var parsed = FileNameParser.Parse(ownFolder ? folderName : videoBase);
        var art = LocalArtwork.Find(files.Select(f => f.Name).ToList(), videoBase, ownFolder);
        var videoRel = RelPath.Combine(rel, video.Name);

        var movie = new Movie
        {
            RootId = root.Id,
            Folder = rel,
            VideoFile = videoRel,
            VideoSize = video.Length,
            Stamp = stamp,
            Title = nfo?.Title ?? parsed.Title,
            OriginalTitle = nfo?.OriginalTitle,
            SortTitle = nfo?.SortTitle,
            Year = nfo?.Year ?? parsed.Year ?? YearOf(nfo?.Premiered),
            Plot = nfo?.Plot,
            Tagline = nfo?.Tagline,
            Runtime = nfo?.Runtime is > 0 ? nfo.Runtime : null,
            Rating = nfo?.Rating is > 0 ? nfo.Rating : null,
            CriticRating = nfo?.CriticRating,
            Mpaa = nfo?.Mpaa,
            Premiered = nfo?.Premiered,
            ImdbId = nfo?.ImdbId ?? parsed.ImdbId,
            TmdbId = nfo?.TmdbId ?? parsed.TmdbId,
            Collection = nfo?.Collection,
            Genres = Movie.Join(nfo?.Genres ?? []),
            Studios = Movie.Join(nfo?.Studios ?? []),
            Directors = Movie.Join(nfo?.Directors ?? []),
            Countries = Movie.Join(nfo?.Countries ?? []),
            PosterFile = art.Poster is null ? null : RelPath.Combine(rel, art.Poster),
            FanartFile = art.Fanart is null ? null : RelPath.Combine(rel, art.Fanart),
            LogoFile = art.Logo is null ? null : RelPath.Combine(rel, art.Logo),
            Resolution = ResolutionLabel(nfo?.VideoWidth, nfo?.VideoHeight),
            VideoCodec = nfo?.VideoCodec,
            AudioCodec = nfo?.AudioCodec,
            AudioChannels = nfo?.AudioChannels,
            Subtitles = Movie.Join(files.Select(f => MediaFiles.SubtitleTag(f.Name, videoBase))
                                        .Where(t => t is not null)
                                        .Select(t => t!.Length == 0 ? "und" : t)),
            OtherVideos = Movie.Join(otherVideos),
            Watched = nfo?.Watched ?? false,
            DateAdded = new DateTimeOffset(Earliest(nfo?.DateAdded?.ToUniversalTime(), video.LastWriteTimeUtc)).ToUnixTimeSeconds(),
        };

        if (byVideo.TryGetValue(videoRel, out var known))
        {
            movie.Id = known.Id;
            movie.Watched = known.Watched;
            movie.DateAdded = known.DateAdded;
        }

        if (art.Poster is not null)
        {
            var posterInfo = files.First(f => f.Name == art.Poster);
            var key = ImageCache.KeyFor(posterInfo.FullName, posterInfo.Length, posterInfo.LastWriteTimeUtc);
            movie.ThumbKey = posters.Ensure(key, posterInfo.FullName) ? key : null;
        }

        var cast = (nfo?.Actors ?? []).Take(40)
                                      .Select(a => new CastMember { Name = a.Name, Role = a.Role, TmdbId = a.TmdbId, Thumb = a.Thumb })
                                      .ToList();
        online.Apply(movie, cast, titleIsGuess: nfo?.Title is null, posters);
        return (movie, cast);
    }

    /// <summary>
    /// Builds some movies again from their folders, with what is now known online: after a
    /// download from TMDB, or after the user chose which movie a file is.
    /// </summary>
    public async Task<int> RebuildAsync(LibraryRoot root, IReadOnlyCollection<Movie> movies, CancellationToken ct)
    {
        var online = OnlineData.Load(db, root.Id);
        var byVideo = db.GetMovies(root.Id).ToDictionary(m => m.VideoFile, StringComparer.OrdinalIgnoreCase);
        var found = new ConcurrentBag<(Movie, IReadOnlyList<CastMember>)>();
        List<FileInfo>? rootFiles = null;
        if (movies.Any(m => m.Folder.Length == 0))
            rootFiles = await Task.Run(() => new DirectoryInfo(ExactPath.ForIo(root.Path)).EnumerateFiles().ToList(), ct);

        await Parallel.ForEachAsync(movies, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            (m, _) =>
            {
                try
                {
                    if (m.Folder.Length > 0)
                    {
                        var dir = new DirectoryInfo(RelPath.ToIo(root.Path, m.Folder));
                        dir.Refresh();
                        if (!dir.Exists)
                            return ValueTask.CompletedTask;
                        var files = dir.EnumerateFiles().ToList();
                        var video = files.FirstOrDefault(f => RelPath.Combine(m.Folder, f.Name).Equals(m.VideoFile, StringComparison.OrdinalIgnoreCase));
                        if (video is null)
                            return ValueTask.CompletedTask;
                        var others = files.Where(f => f != video && MediaFiles.IsVideo(f.Name) && !MediaFiles.IsExtraVideo(f.Name))
                                          .Select(f => RelPath.Combine(m.Folder, f.Name)).ToList();
                        found.Add(Build(root, m.Folder, files, video, others, ownFolder: true, dir.LastWriteTimeUtc.Ticks, byVideo, online));
                    }
                    else if (rootFiles?.FirstOrDefault(f => f.Name.Equals(m.VideoFile, StringComparison.OrdinalIgnoreCase)) is { } video)
                    {
                        var baseName = Path.GetFileNameWithoutExtension(video.Name);
                        var siblings = rootFiles.Where(f => f.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)).ToList();
                        found.Add(Build(root, "", siblings, video, [], ownFolder: false, siblings.Max(f => f.LastWriteTimeUtc.Ticks), byVideo, online));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
                return ValueTask.CompletedTask;
            });

        db.SaveMovies(found);
        return found.Count;
    }

    private static DateTime Earliest(DateTime? a, DateTime b) => a is { } x && x < b && x.Year > 1990 ? x : b;

    private static int? YearOf(string? date) => date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;

    /// <summary>By width, so a 1920×800 scope movie is still 1080p.</summary>
    public static string? ResolutionLabel(int? width, int? height)
    {
        if (width is not > 0)
            return null;
        return width switch
        {
            >= 3200 => "4K",
            >= 1800 => "1080p",
            >= 1200 => "720p",
            _ when height >= 540 => "576p",
            _ => "SD",
        };
    }

    private static bool IsUnder(string folder, string rel) =>
        folder.Equals(rel, StringComparison.OrdinalIgnoreCase)
        || folder.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> IsReachableAsync(string path, CancellationToken ct)
    {
        // A sleeping NAS can take a while to answer; a missing one can hang for much longer
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

using Kinora.Core.Data;
using Kinora.Core.Models;
using Kinora.Core.Scanning;

namespace Kinora.Core.Moving;

/// <summary>What a delete would remove, for the confirmation; <see cref="Refusal"/> when it must not happen.</summary>
/// <param name="Folder">The folder that goes, or null when only <see cref="Files"/> go (the video first).</param>
/// <param name="UnknownVideos">Videos in the folder that the library doesn't count as this title (a movie folder
/// inside another one isn't read): they go too, so the question names them.</param>
public sealed record DeletePlan(string? Folder, IReadOnlyList<string> Files, long Size, int FileCount, string? Refusal,
                                IReadOnlyList<string>? UnknownVideos = null)
{
    public bool CanDelete => Refusal is null;

    /// <summary>The path to show: the folder, or the video for loose files.</summary>
    public string Where => Folder ?? (Files.Count > 0 ? Files[0] : "");
}

/// <param name="Partly">Not everything went, but something did: the library has been read again from the disk.</param>
public sealed record DeleteResult(bool Deleted, string? Error = null, bool Partly = false);

/// <summary>
/// Deletes a movie, a show or an episode from the disk (for good: not to a recycle bin, which
/// network shares don't have) and then from the library. A movie or a show in its own folder
/// goes with the whole folder; a loose movie or an episode with the small files named like its
/// video (nfo, subtitles, thumb), never with another video. Never a library folder, never a folder
/// that holds other titles or another library folder; videos in the folder that the library
/// doesn't count as the title are listed in the plan, for the question.
/// </summary>
/// <remarks>
/// Paths are put together as they are, never normalized: Windows would drop the final dot of
/// "S.W.A.T." and land on a folder "S.W.A.T" next to it. Files are reached through "\\?\" paths
/// for the same reason.
/// </remarks>
public sealed class LibraryDeleter(LibraryDatabase db, MovieScanner movieScanner, SeriesScanner seriesScanner)
{
    /// <summary>Bigger than any nfo, subtitle or picture: a file like this next to a video is another video.</summary>
    private const long CompanionMaxSize = 20L * 1024 * 1024;

    /// <summary>Inside the title's folder only: a junction there is deleted as a link, never followed.</summary>
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    // ---- plans ----

    public DeletePlan PlanMovie(Movie movie, LibraryRoot root) => movie.Folder.Length > 0
        ? FolderPlan(root, movie.Folder, movie.OtherVideoList.Prepend(movie.VideoFile), movieId: movie.Id, seriesId: null)
        : FilesPlan(root, movie.VideoFile);

    public DeletePlan PlanSeries(Series series, LibraryRoot root) => series.Folder.Length == 0
        ? Refused("The show is the library folder itself.")
        : FolderPlan(root, series.Folder, db.GetEpisodes(series.Id).Select(e => e.VideoFile), movieId: null, seriesId: series.Id);

    public DeletePlan PlanEpisode(Episode episode, LibraryRoot root) => FilesPlan(root, episode.VideoFile);

    private DeletePlan FolderPlan(LibraryRoot root, string rel, IEnumerable<string> knownVideos, long? movieId, long? seriesId)
    {
        if (!IsSafeRelative(rel))
            return Refused("The folder is not inside the library folder.");
        var folder = Under(RootOf(root), rel);
        // A last guard on the path itself: strictly below the library folder
        if (!IsStrictlyInside(folder, root.Path))
            return Refused("The folder is not inside the library folder.");
        if (OthersInside(root, folder, movieId, seriesId) is { } others)
            return Refused(others);
        var io = ForIo(folder);
        var dir = new DirectoryInfo(io);
        if (!dir.Exists)
            return Refused("The folder is not there any more.");
        // Deleting through a link would empty what it points to and leave the link
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return Refused("The folder is a link to another place.");

        var files = dir.EnumerateFiles("*", AllFiles).ToList();
        var known = knownVideos.Where(v => v.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase))
                               .Select(v => v[(rel.Length + 1)..].Replace('/', Path.DirectorySeparatorChar))
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = files.Where(f => MediaFiles.IsVideo(f.Name) && !MediaFiles.IsExtraVideo(f.Name))
                           .Select(f => f.FullName[(io.Length + 1)..])
                           .Where(r => !known.Contains(r))
                           // Trailers, featurettes and the like in their usual folders belong to the title
                           .Where(r => !(Path.GetDirectoryName(r) ?? "").Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                                                                        .Any(MediaFiles.IsIgnoredFolder))
                           .ToList();
        return new DeletePlan(folder, [], files.Sum(f => f.Length), files.Count, null, unknown);
    }

    private static DeletePlan FilesPlan(LibraryRoot root, string relVideo)
    {
        if (!IsSafeRelative(relVideo))
            return Refused("The file is not inside the library folder.");
        var video = Under(RootOf(root), relVideo);
        if (!File.Exists(ForIo(video)))
            return Refused("The file is not there any more.");
        var files = OwnFiles(video);
        return new DeletePlan(null, files.Select(f => f.Path).ToList(), files.Sum(f => f.Size), files.Count, null);
    }

    /// <summary>
    /// Other titles of any library folder, or a library folder, inside this folder; or another
    /// library folder between its own and it: then it stays.
    /// </summary>
    private string? OthersInside(LibraryRoot own, string folder, long? movieId, long? seriesId)
    {
        int movies = 0, shows = 0, roots = 0;
        var ownFull = RootOf(own);
        foreach (var r in db.GetRoots())
        {
            var rootFull = RootOf(r);
            if (IsWithin(rootFull, folder))
                roots++;
            // "\\nas\video\TV\Show" as a movie of "\\nas\video", with the series library "\\nas\video\TV"
            if (r.Id != own.Id && !rootFull.Equals(ownFull, StringComparison.OrdinalIgnoreCase)
                && IsWithin(rootFull, ownFull) && IsWithin(folder, rootFull))
                return $"It is inside another library folder, {r.Name}.";
            movies += db.GetMovies(r.Id).Count(m => m.Id != movieId && IsWithin(Under(rootFull, m.VideoFile), folder));
            shows += db.GetSeries(r.Id).Count(s => s.Id != seriesId && IsWithin(Under(rootFull, s.Folder), folder));
        }
        var parts = new List<string>();
        if (movies > 0)
            parts.Add(Plural(movies, "other movie", "other movies"));
        if (shows > 0)
            parts.Add(Plural(shows, "other show", "other shows"));
        if (roots > 0)
            parts.Add(Plural(roots, "library folder", "library folders"));
        return parts.Count == 0 ? null : $"{string.Join(" and ", parts)} inside its folder.";
    }

    /// <summary>
    /// "Show S01E01.mkv": itself first, then Show S01E01.nfo, Show S01E01.en.srt, Show S01E01-thumb.jpg;
    /// not "Show S01E01-E02.mkv" or "Show S01E01.E02.mkv" or their files, nor any other video
    /// (a big file counts as one whatever its extension).
    /// </summary>
    public static List<(string Path, long Size)> OwnFiles(string video)
    {
        var folder = Path.GetDirectoryName(video)!;
        var videoName = Path.GetFileName(video);
        var baseName = Path.GetFileNameWithoutExtension(video);
        var all = new DirectoryInfo(ForIo(folder)).EnumerateFiles().ToList();
        // Videos named like this one and more keep their own files
        var longer = all.Where(f => MediaFiles.IsVideo(f.Name)).Select(f => Path.GetFileNameWithoutExtension(f.Name))
                        .Where(b => b.Length > baseName.Length && b.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
        var own = all.FirstOrDefault(f => f.Name.Equals(videoName, StringComparison.OrdinalIgnoreCase));
        var files = new List<(string, long)> { (video, own?.Length ?? 0) };
        foreach (var f in all)
        {
            if (f.Name.Equals(videoName, StringComparison.OrdinalIgnoreCase) || MediaFiles.IsVideo(f.Name) || f.Length > CompanionMaxSize
                || !BelongsTo(f.Name, baseName) || longer.Any(b => BelongsTo(f.Name, b)))
                continue;
            files.Add((Path.Combine(folder, f.Name), f.Length));
        }
        return files;
    }

    /// <summary>"Heat.en.srt" and "Heat-poster.jpg" belong to "Heat"; "Heat-2.nfo" and "Heat 2.nfo" don't.</summary>
    private static bool BelongsTo(string name, string baseName)
    {
        if (name.Length <= baseName.Length || !name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = name[baseName.Length..];
        return rest[0] == '.' || rest[0] == '-' && rest[1..].Split('.')[0] is { Length: > 0 } tag && tag.All(char.IsLetter);
    }

    // ---- deleting ----

    public Task<DeleteResult> DeleteMovieAsync(Movie movie, LibraryRoot root) => Guarded(async () =>
    {
        var result = Execute(PlanMovie(movie, root));
        if (result.Deleted || result.Partly)
        {
            if (!IsReachable(root))
                return result;   // the share went away halfway: the next scan tells what is there
            if (!File.Exists(ForIo(Under(RootOf(root), movie.VideoFile))))
                db.DeleteMovies([movie.Id]);
            else if (db.GetMovie(movie.Id) is { } left)
                await movieScanner.RebuildAsync(root, [left], CancellationToken.None);
        }
        return result;
    });

    public Task<DeleteResult> DeleteSeriesAsync(Series series, LibraryRoot root) => Guarded(async () =>
    {
        var plan = PlanSeries(series, root);
        var result = Execute(plan);
        if (result.Deleted || result.Partly)
        {
            if (!IsReachable(root))
                return result;
            if (!Directory.Exists(ForIo(plan.Folder!)))
                db.DeleteSeries([series.Id]);
            else if (db.GetSeriesById(series.Id) is { } left)
                await seriesScanner.RebuildAsync(root, [left], CancellationToken.None);
        }
        return result;
    });

    public Task<DeleteResult> DeleteEpisodeAsync(Series series, Episode episode, LibraryRoot root) => Guarded(async () =>
    {
        var result = Execute(PlanEpisode(episode, root));
        // The show is read again: the episode goes, the rest stays as it was
        if ((result.Deleted || result.Partly) && IsReachable(root) && db.GetSeriesById(series.Id) is { } fresh)
            await seriesScanner.RebuildAsync(root, [fresh], CancellationToken.None);
        return result;
    });

    /// <summary>The share can go away at any moment: that is an answer, not a crash.</summary>
    private static Task<DeleteResult> Guarded(Func<Task<DeleteResult>> work) => Task.Run(async () =>
    {
        try
        {
            return await work();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DeleteResult(false, e.Message);
        }
    });

    private static DeleteResult Execute(DeletePlan plan)
    {
        if (plan.Refusal is { } refusal)
            return new DeleteResult(false, refusal);
        var removed = 0;
        try
        {
            if (plan.Folder is { } folder)
            {
                var io = ForIo(folder);
                // Gone since the question (or the share is): nothing was deleted here
                if (!Directory.Exists(io))
                    return new DeleteResult(false, "The folder is not there any more.");
                // Videos first: one that is playing can't go, and then nothing else has been touched
                foreach (var video in Directory.EnumerateFiles(io, "*", AllFiles).Where(f => MediaFiles.IsVideo(f)).ToList())
                {
                    Remove(video);
                    removed++;
                }
                FolderMover.DeleteTree(io);
            }
            else
            {
                foreach (var file in plan.Files)   // the video is the first
                {
                    Remove(ForIo(file));
                    removed++;
                }
            }
            return new DeleteResult(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var partly = removed > 0 || plan.Folder is { } f && CountFiles(ForIo(f)) < plan.FileCount;
            return new DeleteResult(false, e.Message, partly);
        }
    }

    private static void Remove(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static int CountFiles(string folder)
    {
        try
        {
            // A folder that is gone can't be told from a share that is gone: unknown, so not "partly"
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", AllFiles).Count() : int.MaxValue;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return int.MaxValue;
        }
    }

    /// <summary>The library folder answers: only then does a missing file mean deleted, not offline.</summary>
    private static bool IsReachable(LibraryRoot root)
    {
        try
        {
            return Directory.Exists(ForIo(RootOf(root)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- paths ----

    /// <summary>The library folder, absolute; "H:\" keeps its separator, others have none at the end.</summary>
    private static string RootOf(LibraryRoot root)
    {
        var full = Path.GetFullPath(root.Path);
        return Path.GetPathRoot(full) == full ? full : Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>Root + relative path, joined as they are (no normalization).</summary>
    private static string Under(string rootFull, string rel)
    {
        if (rel.Length == 0)
            return rootFull;
        var tail = rel.Replace('/', Path.DirectorySeparatorChar);
        return Path.EndsInDirectorySeparator(rootFull) ? rootFull + tail : rootFull + Path.DirectorySeparatorChar + tail;
    }

    /// <summary>"Alien Collection/Alien (1979)": yes; "", "../x", "a//b", "./a", "C:/x": no.</summary>
    internal static bool IsSafeRelative(string rel) =>
        rel.Length > 0 && !Path.IsPathRooted(rel) && !rel.Contains(':') && !rel.Contains('\\')
        && rel.Split('/').All(s => s.Length > 0 && s != "." && s != "..");

    /// <summary>
    /// The same path for the file functions, untouched: "\\?\H:\S.W.A.T." keeps its dot,
    /// "\\?\UNC\nas\video\…" for shares. Elsewhere than Windows paths are taken as they are.
    /// </summary>
    internal static string ForIo(string path)
    {
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    /// <summary>@"H:\Heat (1995)" is inside @"H:\"; @"H:\" itself, @"H:\Movies\..\x" and @"H:\Movies2" for @"H:\Movies" are not.</summary>
    public static bool IsStrictlyInside(string fullPath, string rootPath)
    {
        var rootFull = Path.GetFullPath(rootPath);
        if (!Path.EndsInDirectorySeparator(rootFull))
            rootFull += Path.DirectorySeparatorChar;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        return full.Length > rootFull.Length && full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path is the folder or below it (both as joined by <see cref="Under"/>).</summary>
    private static bool IsWithin(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static DeletePlan Refused(string why) => new(null, [], 0, 0, why);

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}

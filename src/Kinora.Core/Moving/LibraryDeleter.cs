using Kinora.Core.Data;
using Kinora.Core.Models;
using Kinora.Core.Scanning;

namespace Kinora.Core.Moving;

/// <summary>What a delete would remove, for the confirmation; <see cref="Refusal"/> when it must not happen.</summary>
/// <param name="UnknownVideos">Videos in the folder that the library doesn't count as this title (a movie folder
/// inside another one isn't read): they go too, so the question names them.</param>
public sealed record DeletePlan(string? Folder, IReadOnlyList<string> Files, long Size, int FileCount, string? Refusal,
                                IReadOnlyList<string>? UnknownVideos = null)
{
    public bool CanDelete => Refusal is null;

    /// <summary>The path to show: the folder, or the video for loose files.</summary>
    public string Where => Folder ?? (Files.Count > 0 ? Files[0] : "");
}

public sealed record DeleteResult(bool Deleted, string? Error = null);

/// <summary>
/// Deletes a movie, a show or an episode from the disk (for good: not to a recycle bin, which
/// network shares don't have) and then from the library. A movie or a show in its own folder
/// goes with the whole folder; a loose movie or an episode with the files named like its video
/// (nfo, subtitles, thumb), never with another video. Never the library folder itself, and
/// never a folder that holds other titles of the library; videos in the folder that the library
/// doesn't count as the title are listed in the plan, for the question.
/// </summary>
public sealed class LibraryDeleter(LibraryDatabase db, SeriesScanner seriesScanner)
{
    public DeletePlan PlanMovie(Movie movie, LibraryRoot root)
    {
        if (movie.Folder.Length > 0)
        {
            var others = db.GetMovies(root.Id).Count(m => m.Id != movie.Id && IsInside(m.VideoFile, movie.Folder));
            return others > 0
                ? Refused($"{Plural(others, "other movie is", "other movies are")} inside its folder.")
                : FolderPlan(root, movie.Folder, movie.OtherVideoList.Prepend(movie.VideoFile));
        }
        return FilesPlan(RelPath.ToFull(root.Path, movie.VideoFile));
    }

    public DeletePlan PlanSeries(Series series, LibraryRoot root)
    {
        if (series.Folder.Length == 0)
            return Refused("The show is the library folder itself.");
        var others = db.GetSeries(root.Id).Count(s => s.Id != series.Id && IsInside(s.Folder, series.Folder));
        return others > 0
            ? Refused($"{Plural(others, "other show is", "other shows are")} inside its folder.")
            : FolderPlan(root, series.Folder, db.GetEpisodes(series.Id).Select(e => e.VideoFile));
    }

    public DeletePlan PlanEpisode(Episode episode, LibraryRoot root) =>
        FilesPlan(RelPath.ToFull(root.Path, episode.VideoFile));

    public Task<DeleteResult> DeleteMovieAsync(Movie movie, LibraryRoot root) => Task.Run(() =>
    {
        var plan = PlanMovie(movie, root);
        var result = Execute(plan);
        if (!File.Exists(RelPath.ToFull(root.Path, movie.VideoFile)))
            db.DeleteMovies([movie.Id]);
        return result;
    });

    public Task<DeleteResult> DeleteSeriesAsync(Series series, LibraryRoot root) => Task.Run(() =>
    {
        var plan = PlanSeries(series, root);
        var result = Execute(plan);
        if (plan.Folder is not null && !Directory.Exists(plan.Folder))
            db.DeleteSeries([series.Id]);
        return result;
    });

    public async Task<DeleteResult> DeleteEpisodeAsync(Series series, Episode episode, LibraryRoot root)
    {
        var result = await Task.Run(() => Execute(PlanEpisode(episode, root)));
        // The show is read again: the episode goes, the rest stays as it was
        if (db.GetSeriesById(series.Id) is { } fresh)
            await seriesScanner.RebuildAsync(root, [fresh], CancellationToken.None);
        return result;
    }

    private static DeleteResult Execute(DeletePlan plan)
    {
        if (plan.Refusal is { } refusal)
            return new DeleteResult(false, refusal);
        try
        {
            if (plan.Folder is { } folder)
            {
                if (Directory.Exists(folder))
                    FolderMover.DeleteTree(folder);
            }
            else
            {
                foreach (var file in plan.Files)
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
            return new DeleteResult(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DeleteResult(false, e.Message);
        }
    }

    private static DeletePlan FolderPlan(LibraryRoot root, string relFolder, IEnumerable<string> knownVideos)
    {
        var full = Path.GetFullPath(RelPath.ToFull(root.Path, relFolder));
        // A last guard on the path itself: strictly below the library folder
        if (!IsStrictlyInside(full, root.Path))
            return Refused("The folder is not inside the library folder.");
        var dir = new DirectoryInfo(full);
        if (!dir.Exists)
            return Refused("The folder is not there any more.");
        var files = dir.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var known = knownVideos.Select(v => Path.GetFullPath(RelPath.ToFull(root.Path, v))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = files.Where(f => MediaFiles.IsVideo(f.Name) && !known.Contains(f.FullName) && !MediaFiles.IsExtraVideo(f.Name))
                           .Select(f => Path.GetRelativePath(full, f.FullName))
                           // Trailers, featurettes and the like in their usual folders belong to the title
                           .Where(rel => !(Path.GetDirectoryName(rel) ?? "").Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                                                                          .Any(MediaFiles.IsIgnoredFolder))
                           .ToList();
        return new DeletePlan(full, [], files.Sum(f => f.Length), files.Count, null, unknown);
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

    private static DeletePlan FilesPlan(string video)
    {
        if (!File.Exists(video))
            return Refused("The file is not there any more.");
        var files = OwnFiles(video);
        return new DeletePlan(null, files, files.Sum(f => new FileInfo(f).Length), files.Count, null);
    }

    /// <summary>
    /// "Show S01E01.mkv": itself, Show S01E01.nfo, Show S01E01.en.srt, Show S01E01-thumb.jpg; not
    /// "Show S01E01-E02.mkv" or its files, nor any other video.
    /// </summary>
    public static List<string> OwnFiles(string video)
    {
        var folder = Path.GetDirectoryName(video)!;
        var baseName = Path.GetFileNameWithoutExtension(video);
        var all = Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)!).ToList();
        // Videos named like this one and more ("Show S01E01.E02.mkv") keep their own files
        var longer = all.Where(MediaFiles.IsVideo).Select(n => Path.GetFileNameWithoutExtension(n)!)
                        .Where(b => b.Length > baseName.Length && b.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
        var files = new List<string> { video };
        foreach (var name in all)
        {
            if (name.Equals(Path.GetFileName(video), StringComparison.OrdinalIgnoreCase) || MediaFiles.IsVideo(name)
                || !BelongsTo(name, baseName) || longer.Any(b => BelongsTo(name, b)))
                continue;
            files.Add(Path.Combine(folder, name));
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

    private static bool IsInside(string rel, string folder) =>
        rel.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) || rel.Equals(folder, StringComparison.OrdinalIgnoreCase);

    private static DeletePlan Refused(string why) => new(null, [], 0, 0, why);

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}

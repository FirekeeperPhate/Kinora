using Kinora.Core.Data;
using Kinora.Core.Models;
using Kinora.Core.Scanning;

namespace Kinora.Core.Moving;

/// <summary>
/// Moves a movie or a show to another library folder of the same kind, on disk and in the
/// library: the row keeps its id, watched flags and TMDB match, and is read again from its new
/// place. A movie in its own folder moves with the whole folder (subtitles, nfo, pictures,
/// extras); a loose one moves with the files named like it.
/// </summary>
public sealed class LibraryMover(LibraryDatabase db, MovieScanner movieScanner, SeriesScanner seriesScanner)
{
    public async Task<MoveResult> MoveMovieAsync(Movie movie, LibraryRoot from, LibraryRoot to, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        MoveResult result;
        string newFolder;
        if (movie.Folder.Length > 0)
        {
            newFolder = RelPath.LastSegment(movie.Folder);
            result = await FolderMover.MoveFolderAsync(RelPath.ToFull(from.Path, movie.Folder), to.Path, progress, ct);
        }
        else
        {
            newFolder = "";
            var files = CompanionFiles(from.Path, movie.VideoFile);
            result = await FolderMover.MoveFilesAsync(files, to.Path, progress, ct);
        }

        if (result.Outcome is MoveOutcome.Moved or MoveOutcome.MovedSourceLeft)
        {
            db.MoveMovie(movie, to.Id, newFolder);
            if (db.GetMovie(movie.Id) is { } moved)
                await movieScanner.RebuildAsync(to, [moved], CancellationToken.None);
        }
        return result;
    }

    public async Task<MoveResult> MoveSeriesAsync(Series series, LibraryRoot from, LibraryRoot to, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        var newFolder = RelPath.LastSegment(series.Folder);
        var result = await FolderMover.MoveFolderAsync(RelPath.ToFull(from.Path, series.Folder), to.Path, progress, ct);
        if (result.Outcome is MoveOutcome.Moved or MoveOutcome.MovedSourceLeft)
        {
            db.MoveSeries(series, to.Id, newFolder);
            if (db.GetSeriesById(series.Id) is { } moved)
                await seriesScanner.RebuildAsync(to, [moved], CancellationToken.None);
        }
        return result;
    }

    /// <summary>How much a move would copy, for the confirmation.</summary>
    public static long SizeOf(Movie movie, LibraryRoot root)
    {
        if (movie.Folder.Length > 0)
        {
            var dir = new DirectoryInfo(RelPath.ToFull(root.Path, movie.Folder));
            return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        return CompanionFiles(root.Path, movie.VideoFile).Sum(f => new FileInfo(f).Length);
    }

    public static long SizeOf(Series series, LibraryRoot root)
    {
        var dir = new DirectoryInfo(RelPath.ToFull(root.Path, series.Folder));
        return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    /// <summary>"Heat.mkv" loose in a root: Heat.mkv, Heat.nfo, Heat.en.srt, Heat-poster.jpg… but not "Heat 2.mkv".</summary>
    public static List<string> CompanionFiles(string folder, string videoFile)
    {
        var baseName = Path.GetFileNameWithoutExtension(videoFile);
        return Directory.EnumerateFiles(folder)
                        .Where(f =>
                        {
                            var name = Path.GetFileName(f);
                            if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                                return false;
                            var rest = name[baseName.Length..];
                            return rest.Length > 0 && rest[0] is '.' or '-';
                        })
                        .ToList();
    }
}

namespace Kinora.Core.Scanning;

public sealed record ArtworkFiles(string? Poster, string? Fanart, string? Logo);

/// <summary>
/// Finds poster, fanart and logo among the files of a movie folder, using the names Kodi, Emby
/// and Jellyfin write. Works on the folder listing already in hand: no extra trips to the NAS.
/// </summary>
public static class LocalArtwork
{
    public static ArtworkFiles Find(IReadOnlyCollection<string> fileNames, string videoBaseName, bool ownFolder)
    {
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileNames)
        {
            if (!MediaFiles.ImageExtensions.Contains(Path.GetExtension(name)))
                continue;
            byStem.TryAdd(Path.GetFileNameWithoutExtension(name), name);
            // "Movie.tbn" is a poster too, but its stem is the video's own name
            if (Path.GetExtension(name).Equals(".tbn", StringComparison.OrdinalIgnoreCase))
                byStem.TryAdd(Path.GetFileNameWithoutExtension(name) + "#tbn", name);
        }

        string? First(params string[] stems)
        {
            foreach (var stem in stems)
                if (byStem.TryGetValue(stem, out var file))
                    return file;
            return null;
        }

        var b = videoBaseName;
        if (ownFolder)
        {
            return new ArtworkFiles(
                First("poster", "folder", "cover", "default", "movie", b + "-poster", b + "#tbn", "movie#tbn"),
                First("fanart", "backdrop", "background", "art", b + "-fanart", b + "-backdrop", "landscape", b + "-landscape"),
                First("logo", "clearlogo", b + "-logo", b + "-clearlogo"));
        }
        // A video loose in the root shares the folder with others: only names tied to it count
        return new ArtworkFiles(
            First(b + "-poster", b + "#tbn", b + "-cover", b),
            First(b + "-fanart", b + "-backdrop", b + "-landscape"),
            First(b + "-logo", b + "-clearlogo"));
    }
}

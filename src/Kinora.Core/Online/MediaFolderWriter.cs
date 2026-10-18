using System.Text;
using Kinora.Core.Models;

namespace Kinora.Core.Online;

/// <summary>
/// With "save to the movie folders" on: writes the nfo and the pictures a folder lacks, with the
/// names Kodi, Emby and Jellyfin read. Never replaces a file that is already there.
/// </summary>
public static class MediaFolderWriter
{
    public static async Task<int> WriteMissingAsync(LibraryRoot root, Movie m, TmdbMovie t, CancellationToken ct)
    {
        var ownFolder = m.Folder.Length > 0;
        var folder = RelPath.ToIo(root.Path, m.Folder);
        var baseName = Path.GetFileNameWithoutExtension(RelPath.LastSegment(m.VideoFile));
        var existing = new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var written = 0;

        string Name(string ownName, string looseSuffix, string extension) =>
            ownFolder ? ownName + extension : baseName + looseSuffix + extension;

        async Task Picture(string? tmdbPath, string? localFile, string ownName, string looseSuffix, string size)
        {
            if (tmdbPath is null || localFile is not null)
                return;
            var name = Name(ownName, looseSuffix, Path.GetExtension(tmdbPath));
            if (existing.Contains(name))
                return;
            if (await TmdbImages.DownloadAsync(tmdbPath, size, ct) is not { } bytes)
                return;
            if (Save(Path.Combine(folder, name), bytes))
                written++;
        }

        await Picture(t.PosterPath, m.PosterFile, "poster", "-poster", "original");
        await Picture(t.BackdropPath, m.FanartFile, "fanart", "-fanart", "original");
        await Picture(t.LogoPath, m.LogoFile, "logo", "-logo", "original");

        var hasNfo = existing.Contains(baseName + ".nfo") || (ownFolder && existing.Contains("movie.nfo"));
        if (!hasNfo)
        {
            var nfoName = ownFolder ? "movie.nfo" : baseName + ".nfo";
            if (Save(Path.Combine(folder, nfoName), new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(NfoWriter.Write(t))).ToArray()))
                written++;
        }
        return written;
    }

    /// <summary>Written aside and moved in place: a half-written file never takes a good name.</summary>
    private static bool Save(string path, byte[] data)
    {
        if (File.Exists(path))
            return false;
        var temp = path + ".kinora.tmp";
        File.WriteAllBytes(temp, data);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            File.Delete(temp);
            return false;
        }
    }
}

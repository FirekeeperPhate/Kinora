using Dapper;
using Kinora.Core.Models;

namespace Kinora.Core.Data;

/// <summary>After a move on disk: the same rows, pointing to the new root and folder.</summary>
public sealed partial class LibraryDatabase
{
    /// <summary>"Alien Collection/Alien (1979)/Alien.mkv" with "Alien Collection/Alien (1979)" → "Alien (1979)" becomes "Alien (1979)/Alien.mkv".</summary>
    public static string? Reprefix(string? rel, string oldPrefix, string newPrefix)
    {
        if (rel is null)
            return null;
        if (oldPrefix.Length == 0)
            return newPrefix.Length == 0 ? rel : newPrefix + "/" + rel;
        if (rel.Equals(oldPrefix, StringComparison.OrdinalIgnoreCase))
            return newPrefix;
        if (rel.StartsWith(oldPrefix + "/", StringComparison.OrdinalIgnoreCase))
            return newPrefix.Length == 0 ? rel[(oldPrefix.Length + 1)..] : newPrefix + rel[oldPrefix.Length..];
        return rel;
    }

    public void MoveMovie(Movie m, long newRootId, string newFolder)
    {
        string? R(string? rel) => Reprefix(rel, m.Folder, newFolder);
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            UPDATE matches SET root_id = @newRootId, video_file = @newVideo WHERE root_id = @oldRootId AND video_file = @oldVideo
            """, new { newRootId, newVideo = R(m.VideoFile), oldRootId = m.RootId, oldVideo = m.VideoFile }, tx);
        c.Execute("""
            UPDATE movies SET root_id = @newRootId, folder = @folder, video_file = @video, other_videos = @others,
                              poster_file = @poster, fanart_file = @fanart, logo_file = @logo, stamp = 0
            WHERE id = @id
            """, new
        {
            newRootId,
            folder = newFolder,
            video = R(m.VideoFile),
            others = Movie.Join(m.OtherVideoList.Select(v => R(v)!)),
            poster = R(m.PosterFile),
            fanart = R(m.FanartFile),
            logo = R(m.LogoFile),
            id = m.Id,
        }, tx);
        tx.Commit();
    }

    public void MoveSeries(Series s, long newRootId, string newFolder)
    {
        string? R(string? rel) => Reprefix(rel, s.Folder, newFolder);
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("UPDATE matches SET root_id = @newRootId, video_file = @newFolder WHERE root_id = @oldRootId AND video_file = @oldFolder",
                  new { newRootId, newFolder, oldRootId = s.RootId, oldFolder = s.Folder }, tx);
        c.Execute("""
            UPDATE series SET root_id = @newRootId, folder = @newFolder, poster_file = @poster, fanart_file = @fanart, logo_file = @logo, stamp = 0
            WHERE id = @id
            """, new { newRootId, newFolder, poster = R(s.PosterFile), fanart = R(s.FanartFile), logo = R(s.LogoFile), id = s.Id }, tx);
        foreach (var e in c.Query<Episode>("SELECT * FROM episodes WHERE series_id = @id", new { id = s.Id }, tx).ToList())
            c.Execute("UPDATE episodes SET video_file = @video, thumb_file = @thumb WHERE id = @id",
                      new { video = R(e.VideoFile), thumb = R(e.ThumbFile), id = e.Id }, tx);
        foreach (var season in c.Query<Season>("SELECT * FROM seasons WHERE series_id = @id", new { id = s.Id }, tx).ToList())
            c.Execute("UPDATE seasons SET poster_file = @poster WHERE series_id = @id AND number = @number",
                      new { poster = R(season.PosterFile), id = s.Id, number = season.Number }, tx);
        tx.Commit();
    }
}

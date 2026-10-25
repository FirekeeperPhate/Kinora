using Kinora.Core.Data;
using Kinora.Core.Imaging;
using Kinora.Core.Models;

namespace Kinora.Core.Online;

/// <summary>
/// The actors' photos for the cast on a page. In order: the photo the nfo names when it can be
/// reached (a web address, or a file in the title's folder: Jellyfin's paths on its own server
/// can't), Kodi's ".actors/First_Last.jpg", then TMDB's, from the title's credits (asked once
/// and kept in the database). Photos are downloaded once, into the people cache.
/// </summary>
public sealed class CastPhotos(LibraryDatabase db, FileCache people)
{
    /// <summary>Small: they are shown at 48 px. The person page keeps its own, bigger ones.</summary>
    private const string TmdbSize = "w185";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Replaced in tests: the web downloads of nfo photos.</summary>
    internal static Func<string, CancellationToken, Task<byte[]?>>? DownloadOverride { get; set; }

    /// <summary>TMDB's cast of a title: asked once, then read from the database. Empty without a key or an id.</summary>
    public async Task<IReadOnlyList<TmdbCast>> CreditsAsync(TmdbClient? tmdb, bool series, string? tmdbId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tmdbId))
            return [];
        if (db.GetTmdbCredits(series, tmdbId) is { } known)
            return known;
        if (tmdb is null)
            return [];
        try
        {
            var cast = await tmdb.GetCreditsAsync(series, tmdbId, ct);
            db.SaveTmdbCredits(series, tmdbId, cast);
            return cast;
        }
        catch (Exception e) when (e is HttpRequestException or TmdbException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return [];   // offline: no photos this time, asked again next time
        }
    }

    /// <summary>An actor's entry in TMDB's credits: by TMDB id, else by name (nfo files often have no id).</summary>
    public static TmdbCast? Match(CastMember member, IReadOnlyList<TmdbCast> credits)
    {
        if (member.TmdbId is { } id && credits.FirstOrDefault(c => c.Id == id) is { } byId)
            return byId;
        var name = SearchText.Normalize(member.Name);
        return credits.FirstOrDefault(c => SearchText.Normalize(c.Name) == name);
    }

    /// <summary>A file with the actor's photo (downloaded the first time), or null when there is none.</summary>
    public async Task<string?> PhotoAsync(CastMember member, TmdbCast? match, string? titleFolder, CancellationToken ct)
    {
        if (member.Thumb is { Length: > 0 } thumb)
        {
            if (Uri.TryCreate(thumb, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var key = ImageCache.KeyForOnline(thumb);
                if (people.Has(key) || await DownloadAsync(thumb, ct) is { Length: > 0 } bytes && Save(key, bytes))
                    return people.PathFor(key);
            }
            else if (LocalFile(thumb, titleFolder) is { } file)
            {
                return file;
            }
        }
        if (titleFolder is not null && LocalFile(Path.Combine(".actors", member.Name.Replace(' ', '_') + ".jpg"), titleFolder) is { } kodi)
            return kodi;
        if (match?.ProfilePath is { Length: > 0 } path)
        {
            var key = ImageCache.KeyForOnline(TmdbSize + path);
            if (people.Has(key) || await TmdbImages.DownloadAsync(path, TmdbSize, ct) is { Length: > 0 } bytes && Save(key, bytes))
                return people.PathFor(key);
        }
        return null;
    }

    /// <summary>A file the nfo names, relative to the title's folder or absolute, if it is there.</summary>
    private static string? LocalFile(string thumb, string? titleFolder)
    {
        try
        {
            var path = Path.IsPathFullyQualified(thumb) ? thumb
                : titleFolder is null ? null
                : Path.Combine(titleFolder, thumb.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            return path is not null && File.Exists(ExactPath.ForIo(path)) ? ExactPath.ForIo(path) : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool Save(string key, byte[] bytes)
    {
        try
        {
            people.Save(key, bytes);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        if (DownloadOverride is { } fake)
            return await fake(url, ct);
        try
        {
            using var response = await Http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
